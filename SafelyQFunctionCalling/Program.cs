using GenerativeAI;
using GenerativeAI.Core;
using GenerativeAI.Tools;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        var getBusinessTool = new QuickTool(
            GetBusinessInfoAsync,
            "GetBusinessInfoAsync",
            "Search for a business and return details from SafelyQ. Trigger when the user asks about a business by name (e.g., 'tell me about X', 'info on X')."
        );

        var checkAppointmentTool = new QuickTool(
            CheckUserAppointmentAsync,
            // Name nudges routing; keep short and actiony.
            "CheckUserAppointments",
            // Very explicit trigger phrases so Gemini routes correctly.
            "Check a user's booked appointments. Triggers when the user mentions: appointments, my appointments, schedule, bookings, upcoming, see my appointments, check appointments. If the user provides only a date (YYYY-MM-DD), treat that as the date to check. Parameter: Date (YYYY-MM-DD) optional; default to today."
        );

        var model = new GenerativeModel(
            apiKey: "AIzaSyA9whWfyPLWY7HSzAeOV5sb_BX84jvECvc",
            model: GoogleAIModels.Gemini2Flash
        );

        model.AddFunctionTool(getBusinessTool);
        model.AddFunctionTool(checkAppointmentTool);

        Console.WriteLine("💼 Gemini SafelyQ Tools ready. Ask about businesses or appointments:");

        while (true)
        {
            Console.Write("\n> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Trim().ToLower() == "exit") break;

            var result = await model.GenerateContentAsync(input);
            Console.WriteLine("🔹 Gemini Response:");
            Console.WriteLine(result.Text);
        }
    }

    static readonly HttpClient httpClient = new HttpClient();

    // ---------------- GET BUSINESS INFO ----------------
    public static async Task<BusinessInfoResult> GetBusinessInfoAsync([Description("Business query by name")] BusinessInfoQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.BusinessName))
        {
            return new BusinessInfoResult { Result = "Please specify a business name." };
        }

        var searchPayload = new
        {
            query = @"query all($searchBusinessInput: SearchBusinessInput) {
              searchBusinesses(searchBusinessInput: $searchBusinessInput) {
                id name address1 address2 city state zipCode country description
              }
            }",
            operationName = "all",
            variables = new
            {
                searchBusinessInput = new
                {
                    areaText = "",
                    categories = new string[] { },
                    latitude = 0,
                    longitude = 0,
                    radius = 100,
                    locationEnabled = true,
                    searchText = query.BusinessName,
                    tagsText = ""
                }
            }
        };

        var searchResponse = await PostJsonAsync("https://api.chatclb.dev/query", searchPayload);

        if (!searchResponse.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("searchBusinesses", out var businesses) ||
            businesses.ValueKind != JsonValueKind.Array || businesses.GetArrayLength() == 0)
        {
            return new BusinessInfoResult { Result = $"No businesses found for '{query.BusinessName}'." };
        }

        var firstMatch = businesses[0];
        int businessId = firstMatch.GetProperty("id").GetInt32();

        var getBusinessPayload = new
        {
            query = @"{
              getBusinessById(id: " + businessId + @") {
                id name businessVenue {
                  venue { entrances { name } }
                }
                businessCoupons {
                  code title discount discountType isActive startDate endDate
                }
                services {
                  id name
                }
              }
            }"
        };

        var detailsResponse = await PostJsonAsync("https://api.chatclb.dev/query", getBusinessPayload);
        var root = detailsResponse?.RootElement;

        if (!root.HasValue ||
            !root.Value.TryGetProperty("data", out JsonElement data2) ||
            !data2.TryGetProperty("getBusinessById", out JsonElement details))
        {
            return new BusinessInfoResult { Result = "Failed to fetch business details." };
        }

        string name = details.GetProperty("name").GetString() ?? "Unknown";
        JsonElement serviceList = details.GetProperty("services");
        JsonElement couponList = details.GetProperty("businessCoupons");

        var services = new List<string>();
        if (serviceList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in serviceList.EnumerateArray())
            {
                string serviceName = s.GetProperty("name").GetString() ?? "Unknown";
                services.Add(serviceName);
            }
        }

        var coupons = new List<string>();
        if (couponList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in couponList.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                if (c.TryGetProperty("isActive", out JsonElement activeProp) && activeProp.GetBoolean())
                {
                    string title = c.GetProperty("title").GetString() ?? "Unknown";
                    double discount = c.GetProperty("discount").GetDouble();
                    string discountType = c.GetProperty("discountType").GetString() ?? "";
                    coupons.Add($"{title} ({discount}{discountType})");
                }
            }
        }

        return new BusinessInfoResult
        {
            Result = $"📍 Business: {name}\n🛠️ Services: {string.Join(", ", services)}\n🎟️ Active Coupons: {(coupons.Count > 0 ? string.Join(" | ", coupons) : "None")}"
        };
    }

    // ---------------- CHECK USER APPOINTMENTS ----------------
    public static async Task<AppointmentResult> CheckUserAppointmentAsync(
        [Description("Optional date to check (YYYY-MM-DD). If omitted, use today's date (UTC).")]
        AppointmentQuery query)
    {
        string dateToCheck = string.IsNullOrWhiteSpace(query.Date)
            ? DateTime.UtcNow.ToString("yyyy-MM-dd")
            : query.Date;

        string token = await GetAuthTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            return new AppointmentResult { Result = "Failed to authenticate with SafelyQ API." };
        }

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var appointmentPayload = new
        {
            query = @"query GetCurrentUserAppointments ($userInput: UserInput)
            {
                getCurrentUserAppointments(status: ""Booked"", startDate: """ + dateToCheck + @""", userInput: $userInput)
                {
                    id,
                    startTimeOnly,
                    startDateOnly,
                    status,
                    allocatedTimeFormatted,
                    business{
                        name
                    }
                }
            }",
            operationName = "GetCurrentUserAppointments",
            variables = new
            {
                userInput = new
                {
                    phoneNumber = "+12143029325"
                }
            }
        };

        var appointmentResponse = await PostJsonAsync("https://api.chatclb.dev/query", appointmentPayload);

        if (!appointmentResponse.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("getCurrentUserAppointments", out var appts) ||
            appts.ValueKind != JsonValueKind.Array)
        {
            return new AppointmentResult { Result = $"No appointments found or failed to fetch for {dateToCheck}." };
        }

        var results = new List<string>();
        foreach (JsonElement appt in appts.EnumerateArray())
        {
            string businessName = appt.GetProperty("business").GetProperty("name").GetString() ?? "Unknown";
            string time = appt.GetProperty("startTimeOnly").GetString() ?? "Unknown time";
            results.Add($"{businessName} at {time}");
        }

        return new AppointmentResult
        {
            Result = results.Count > 0
                ? $"Appointments on {dateToCheck}:\n" + string.Join("\n", results)
                : $"No appointments found on {dateToCheck}."
        };
    }

    private static async Task<string> GetAuthTokenAsync()
    {
        var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "https://id.chatclb.dev/connect/token");
        tokenRequest.Content = new StringContent(
            "grant_type=client_credentials&client_id=safelyq.api&client_secret=893bfc0b-880c-4f5e-b258-41d007e08860",
            Encoding.UTF8,
            "application/x-www-form-urlencoded"
        );

        var response = await httpClient.SendAsync(tokenRequest);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
    }

    // ---------------- MODELS ----------------
    public class BusinessInfoQuery { public string BusinessName { get; set; } = string.Empty; }
    public class BusinessInfoResult { public string Result { get; set; } = string.Empty; }

    public class AppointmentQuery { public string Date { get; set; } = string.Empty; }
    public class AppointmentResult { public string Result { get; set; } = string.Empty; }

    // ---------------- HELPERS ----------------
    static async Task<JsonDocument> PostJsonAsync(string url, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(url, content);
        var responseString = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(responseString);
    }
}
