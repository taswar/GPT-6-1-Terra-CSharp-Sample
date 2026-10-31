using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

// Pick the example from the command line: dotnet run -- 1, dotnet run -- 2, or dotnet run -- all.
var choice = (args.Length > 0 ? args[0] : "all").TrimStart('-').ToLowerInvariant();
if (choice is not ("1" or "2" or "all"))
{
    Console.WriteLine("""
        Usage: dotnet run -- [1 | 2 | all]
          1    Read a screenshot (expense-form.png)
          2    Review a PDF invoice with a tool call and structured output (invoice.pdf)
          all  Run both (the default)
        """);
    return 1;
}

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build();

var endpoint = (config["AZURE_OPENAI_ENDPOINT"]
    ?? throw new InvalidOperationException(
        "Set AZURE_OPENAI_ENDPOINT with dotnet user-secrets, for example https://YOUR-RESOURCE-NAME.openai.azure.com"))
    .TrimEnd('/');
var deployment = config["AZURE_OPENAI_DEPLOYMENT"] ?? "gpt-6.1-terra";

// The API key is optional. Without one, the app signs in with Entra ID through az login.
var apiKey = config["AZURE_OPENAI_API_KEY"];

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var terra = new TerraClient(http, endpoint, apiKey, new DefaultAzureCredential(), deployment);

if (choice is "1" or "all")
{
    await ScreenshotExample.RunAsync(terra, "expense-form.png", cts.Token);
}

if (choice is "2" or "all")
{
    await InvoiceAgentExample.RunAsync(terra, "invoice.pdf", cts.Token);
}

return 0;

// Example 1: image analysis. Terra reads a screenshot of a web form and says what blocks the user.
static class ScreenshotExample
{
    public static async Task RunAsync(TerraClient terra, string imagePath, CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Example 1: read a screenshot ===");
        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("Run from the project folder, where the sample files are.", imagePath);
        }

        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(imagePath, cancellationToken));
        var body = new JsonObject
        {
            ["model"] = terra.Deployment,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = """
                                This is a screenshot of a web app. In four short lines:
                                1. What is the user trying to do?
                                2. Which values are already filled in?
                                3. What is stopping them from submitting?
                                4. Which control should they use next? Give its exact label.
                                """
                        },
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:{MimeTypeFor(imagePath)};base64,{imageBase64}",
                            // high reads small text such as field hints; low is cheaper for a rough look.
                            ["detail"] = "high"
                        }
                    }
                }
            },
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["text"] = new JsonObject { ["verbosity"] = "low" }
        };

        var stopwatch = Stopwatch.StartNew();
        var usage = new Usage();
        using var response = await terra.PostAsync(body, cancellationToken);
        stopwatch.Stop();
        usage.Add(response.RootElement);

        Console.WriteLine(TerraClient.ReadOutputText(response.RootElement));
        Console.WriteLine();
        Console.WriteLine($"[terra] {usage} elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine();
    }

    // The data URL needs the image's real type. PNG, JPEG, WebP and non-animated GIF are supported.
    static string MimeTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => throw new NotSupportedException($"Unsupported image type: {path}")
    };
}

// Example 2: an invoice agent. PDF input, one tool call, and a structured result in a C# record.
static class InvoiceAgentExample
{
    const string Instructions = """
        You review supplier invoices for accounts payable.
        Read the attached invoice. Always call check_supplier with the supplier name exactly as printed.
        Set requires_approval to true if the total is over 5000, or if the supplier is not on file or not active.
        List anything a reviewer should look at in issues, such as math that doesn't add up or a missing PO number.
        Use an empty list when there is nothing to flag.
        """;

    // The approval rule, applied in code. The model's requires_approval is only a second opinion.
    const decimal ApprovalThreshold = 5000m;

    // A model that still wants tools after this many rounds is stuck; stop and send the invoice to a person.
    const int MaxToolRounds = 5;

    public static async Task RunAsync(TerraClient terra, string pdfPath, CancellationToken cancellationToken)
    {
        Console.WriteLine("=== Example 2: review a PDF invoice ===");
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("Run from the project folder, where the sample files are.", pdfPath);
        }

        var pdfBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(pdfPath, cancellationToken));
        var body = new JsonObject
        {
            ["model"] = terra.Deployment,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_file",
                            ["filename"] = Path.GetFileName(pdfPath),
                            ["file_data"] = $"data:application/pdf;base64,{pdfBase64}"
                        },
                        new JsonObject { ["type"] = "input_text", ["text"] = "Review this invoice." }
                    }
                }
            }
        };
        AddToolsAndFormat(body);

        var stopwatch = Stopwatch.StartNew();
        var usage = new Usage();
        var response = await terra.PostAsync(body, cancellationToken);
        usage.Add(response.RootElement);

        // The tool loop: run every function call Terra asks for, then send the results back.
        // previous_response_id chains the turns on the service, including the reasoning items,
        // but not the instructions, so AddToolsAndFormat sends them again on every turn.
        for (var round = 1; HasToolCalls(response.RootElement); round++)
        {
            if (round > MaxToolRounds)
            {
                response.Dispose();
                throw new InvalidOperationException(
                    $"Terra still asked for tools after {MaxToolRounds} rounds. Send this invoice to a person.");
            }

            var outputs = await RunToolCallsAsync(response.RootElement);
            var next = new JsonObject
            {
                ["model"] = terra.Deployment,
                ["previous_response_id"] = response.RootElement.GetProperty("id").GetString(),
                ["input"] = outputs
            };
            AddToolsAndFormat(next);

            response.Dispose();
            response = await terra.PostAsync(next, cancellationToken);
            usage.Add(response.RootElement);
        }

        stopwatch.Stop();

        var json = TerraClient.ReadOutputText(response.RootElement);
        response.Dispose();

        var review = JsonSerializer.Deserialize<InvoiceReview>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }) ?? throw new InvalidOperationException($"The model returned no invoice review: {json}");

        // The decision: the total from the model, the supplier status from the vendor master.
        var supplier = FindSupplier(review.SupplierName);
        var needsApproval = review.Total > ApprovalThreshold || supplier is not { Status: "active" };

        Console.WriteLine();
        Console.WriteLine($"Supplier:       {review.SupplierName} (status: {supplier?.Status ?? "not on file"})");
        Console.WriteLine($"Invoice:        {review.InvoiceNumber}, dated {review.InvoiceDate}, PO {review.PoNumber}");
        Console.WriteLine($"Total:          {review.Total:N2} {review.Currency}");
        Console.WriteLine($"Needs approval: {needsApproval} (the model said {review.RequiresApproval})");
        if (needsApproval != review.RequiresApproval)
        {
            Console.WriteLine("Warning:        the model disagrees with the approval rule. The rule wins; log this.");
        }
        foreach (var issue in review.Issues)
        {
            Console.WriteLine($"Issue:          {issue}");
        }

        Console.WriteLine();
        Console.WriteLine($"[terra] {usage} elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s");
    }

    static bool HasToolCalls(JsonElement root) =>
        root.GetProperty("output").EnumerateArray().Any(item => item.GetProperty("type").GetString() == "function_call");

    // Runs each function_call in the response and returns the function_call_output items to send back.
    static async Task<JsonArray> RunToolCallsAsync(JsonElement root)
    {
        var outputs = new JsonArray();
        foreach (var call in root.GetProperty("output").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "function_call"))
        {
            var name = call.GetProperty("name").GetString();
            var arguments = JsonNode.Parse(call.GetProperty("arguments").GetString() ?? "{}")!;
            Console.WriteLine($"[tool] {name}({arguments.ToJsonString()})");

            var result = name switch
            {
                "check_supplier" => await CheckSupplierAsync(arguments["supplier_name"]?.GetValue<string>() ?? ""),
                _ => $$"""{"error": "Unknown tool {{name}}"}"""
            };
            Console.WriteLine($"[tool] -> {result}");

            outputs.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = call.GetProperty("call_id").GetString(),
                ["output"] = result
            });
        }

        return outputs;
    }

    // A stand-in for your ERP or vendor master. Replace it with a real lookup.
    static readonly Dictionary<string, Supplier> Suppliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fabrikam Office Supplies"] = new("V-1042", "active"),
        ["Northwind Traders"] = new("V-0877", "on hold")
    };

    static Supplier? FindSupplier(string supplierName) =>
        Suppliers.TryGetValue(supplierName.Trim(), out var supplier) ? supplier : null;

    static Task<string> CheckSupplierAsync(string supplierName)
    {
        var result = FindSupplier(supplierName) is { } supplier
            ? new JsonObject { ["found"] = true, ["supplier_id"] = supplier.Id, ["status"] = supplier.Status }
            : new JsonObject { ["found"] = false };

        return Task.FromResult(result.ToJsonString());
    }

    // Every request in the loop sends the same instructions, tool, reasoning effort and output schema.
    // Instructions don't carry over through previous_response_id, so they go on every turn.
    static void AddToolsAndFormat(JsonObject body)
    {
        body["instructions"] = Instructions;
        body["tools"] = new JsonArray { CheckSupplierTool() };
        body["reasoning"] = new JsonObject { ["effort"] = "low" };
        // Structured output: the final answer must match the InvoiceReview schema.
        body["text"] = new JsonObject { ["format"] = InvoiceReviewFormat(), ["verbosity"] = "low" };
    }

    static JsonObject CheckSupplierTool() => new()
    {
        ["type"] = "function",
        ["name"] = "check_supplier",
        ["description"] = "Looks up a supplier in the vendor master. Returns whether it exists, its ID and its status.",
        ["strict"] = true,
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["supplier_name"] = new JsonObject { ["type"] = "string", ["description"] = "The supplier name as printed on the invoice." }
            },
            ["required"] = new JsonArray("supplier_name"),
            ["additionalProperties"] = false
        }
    };

    // Strict mode needs every property in "required" and additionalProperties set to false.
    static JsonObject InvoiceReviewFormat() => new()
    {
        ["type"] = "json_schema",
        ["name"] = "invoice_review",
        ["strict"] = true,
        ["schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["supplier_name"] = new JsonObject { ["type"] = "string" },
                ["supplier_on_file"] = new JsonObject { ["type"] = "boolean" },
                ["invoice_number"] = new JsonObject { ["type"] = "string" },
                ["invoice_date"] = new JsonObject { ["type"] = "string", ["description"] = "ISO 8601 date, for example 2026-10-12." },
                ["po_number"] = new JsonObject { ["type"] = "string", ["description"] = "Empty if the invoice has no PO number." },
                ["currency"] = new JsonObject { ["type"] = "string", ["description"] = "ISO 4217 code, for example USD." },
                ["total"] = new JsonObject { ["type"] = "number" },
                ["requires_approval"] = new JsonObject { ["type"] = "boolean" },
                ["issues"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            },
            ["required"] = new JsonArray(
                "supplier_name", "supplier_on_file", "invoice_number", "invoice_date",
                "po_number", "currency", "total", "requires_approval", "issues"),
            ["additionalProperties"] = false
        }
    };
}

// The C# shape of the structured output. Property names map to the schema in snake_case.
record InvoiceReview(
    string SupplierName,
    bool SupplierOnFile,
    string InvoiceNumber,
    string InvoiceDate,
    string PoNumber,
    string Currency,
    decimal Total,
    bool RequiresApproval,
    List<string> Issues);

// A vendor master entry.
record Supplier(string Id, string Status);

// Sends requests to the Responses API with an API key or an Entra ID token.
sealed class TerraClient(HttpClient http, string endpoint, string? apiKey, TokenCredential credential, string deployment)
{
    public string Deployment => deployment;

    public async Task<JsonDocument> PostAsync(JsonObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/v1/responses");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Add("api-key", apiKey);
        }
        else
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://ai.azure.com/.default"]),
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        // StringContent sets a fixed Content-Length; JsonContent uses chunked encoding.
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Responses API returned {(int)response.StatusCode}: {text}");
        }

        return JsonDocument.Parse(text);
    }

    public static string ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var convenience) && convenience.ValueKind == JsonValueKind.String)
        {
            return convenience.GetString() ?? string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "message")
            {
                continue;
            }

            foreach (var part in item.GetProperty("content").EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partType) && partType.GetString() == "output_text")
                {
                    builder.Append(part.GetProperty("text").GetString());
                }
            }
        }

        return builder.ToString();
    }
}

// Token usage summed across every call in an example.
sealed class Usage
{
    public int Calls { get; private set; }
    public int Input { get; private set; }
    public int Cached { get; private set; }
    public int Output { get; private set; }
    public int Reasoning { get; private set; }

    public void Add(JsonElement root)
    {
        Calls++;
        if (!root.TryGetProperty("usage", out var usage))
        {
            return;
        }

        Input += Read(usage, "input_tokens");
        Output += Read(usage, "output_tokens");
        if (usage.TryGetProperty("input_tokens_details", out var inputDetails))
        {
            Cached += Read(inputDetails, "cached_tokens");
        }

        if (usage.TryGetProperty("output_tokens_details", out var outputDetails))
        {
            Reasoning += Read(outputDetails, "reasoning_tokens");
        }
    }

    public override string ToString() =>
        $"calls={Calls} input={Input} cached={Cached} output={Output} reasoning={Reasoning}";

    static int Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
