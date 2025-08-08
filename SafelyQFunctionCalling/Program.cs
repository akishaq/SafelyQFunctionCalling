using GenerativeAI;
using GenerativeAI.Core;
using GenerativeAI.Tools;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        var tool = new QuickTool(GetBusinessInfoAsync, "GetBusinessInfoAsync", "Search for a business and return details from SafelyQ");

        var model = new GenerativeModel(
            apiKey: "AIzaSyA9whWfyPLWY7HSzAeOV5sb_BX84jvECvc",
            model: GoogleAIModels.Gemini2Flash
        );

        model.AddFunctionTool(tool);

        Console.WriteLine("💼 Gemini Business Info Tool is ready. Ask about any business:");

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

    // ---------------- Function Tool Logic ----------------

    static readonly HttpClient httpClient = new HttpClient();

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

        var searchResponse = await PostJsonAsync("https://api.safelyq.com/query", searchPayload);

        if (!searchResponse.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("searchBusinesses", out var businesses) ||
            businesses.ValueKind != JsonValueKind.Array || businesses.GetArrayLength() == 0)
        {
            return new BusinessInfoResult { Result = $"No businesses found for '{query.BusinessName}'." };
        }

        var firstMatch = businesses[0];
        int businessId = firstMatch.GetProperty("id").GetInt32();

        // Step 2: Get full business details
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

        var detailsResponse = await PostJsonAsync("https://api.safelyq.com/query", getBusinessPayload);
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

    // ---------------- Models ----------------

    public class BusinessInfoQuery
    {
        public string BusinessName { get; set; } = string.Empty;
    }

    public class BusinessInfoResult
    {
        public string Result { get; set; } = string.Empty;
    }

    // ---------------- Helpers ----------------

    static async Task<JsonDocument?> PostJsonAsync(string url, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync(url, content);
        var responseString = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(responseString);
    }
}
