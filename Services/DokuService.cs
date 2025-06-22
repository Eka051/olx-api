using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace olx_be_api.Services
{
    public class DokuService : IDokuService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly ILogger<DokuService> _logger;

        public DokuService(HttpClient httpClient, IConfiguration configuration, ILogger<DokuService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<DokuPaymentResponse> CreatePayment(DokuPaymentRequest request)
        {
            var dokuConfig = _configuration.GetSection("DokuSettings");
            var clientId = dokuConfig["ClientId"];
            var secretKey = dokuConfig["SecretKey"];
            var apiBaseUrl = dokuConfig["ApiUrl"];
            var callbackUrl = dokuConfig["CallbackUrl"]?.Trim();

            var endpointPath = "/checkout/v1/payment";
            var fullUrl = apiBaseUrl?.TrimEnd('/') + endpointPath;

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secretKey) ||
                string.IsNullOrEmpty(apiBaseUrl) || string.IsNullOrEmpty(callbackUrl))
            {
                _logger.LogError("DokuSettings tidak dikonfigurasi dengan benar.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Konfigurasi DOKU tidak lengkap." };
            }

            var requestId = Guid.NewGuid().ToString();
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var httpMethod = "POST";

            var lineItems = request.LineItems.Select(item => new
            {
                name = item.Name,
                price = item.Price,
                quantity = item.Quantity
            }).ToArray();

            var payload = new
            {
                order = new
                {
                    amount = request.Amount,
                    invoice_number = request.InvoiceNumber,
                    currency = "IDR",
                    callback_url = callbackUrl,
                    line_items = lineItems
                },
                payment = new
                {
                    payment_due_date = 60
                },
                customer = new
                {
                    name = request.CustomerName,
                    email = request.CustomerEmail
                }
            };

            var jsonSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Include,
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new SnakeCaseNamingStrategy()
                }
            };

            var requestJson = JsonConvert.SerializeObject(payload, jsonSettings);

            try
            {
                JsonConvert.DeserializeObject(requestJson);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Generated JSON is invalid: {Json}", requestJson);
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Invalid JSON generated" };
            }

            _logger.LogInformation("=== DOKU SIGNATURE DEBUG ===");
            _logger.LogInformation("1. HTTP Method: {HttpMethod}", httpMethod);
            _logger.LogInformation("2. Endpoint Path: {EndpointPath}", endpointPath);
            _logger.LogInformation("3. Client ID: {ClientId}", clientId);
            _logger.LogInformation("4. Timestamp: {Timestamp}", timestamp);
            _logger.LogInformation("5. Minified JSON: {RequestJson}", requestJson);
            _logger.LogInformation("5a. JSON Length: {Length} characters", requestJson.Length);

            var suspiciousChars = new[] { ';', '\n', '\r', '\t' };
            foreach (var chr in suspiciousChars)
            {
                if (requestJson.Contains(chr))
                {
                    _logger.LogWarning("Found suspicious character '{Char}' in JSON at position {Position}",
                        chr, requestJson.IndexOf(chr));
                }
            }

            string hexBodyHash;
            using (var sha256 = SHA256.Create())
            {
                var bodyBytes = Encoding.UTF8.GetBytes(requestJson);
                var hashBytes = sha256.ComputeHash(bodyBytes);
                hexBodyHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            _logger.LogInformation("6. SHA256 Hash: {Hash}", hexBodyHash);

            var stringToSign = $"{httpMethod}:{endpointPath}:{clientId}:{hexBodyHash}:{timestamp}";

            _logger.LogInformation("7. String to Sign: {StringToSign}", stringToSign);

            string finalSignature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey)))
            {
                var stringToSignBytes = Encoding.UTF8.GetBytes(stringToSign);
                var signatureBytes = hmac.ComputeHash(stringToSignBytes);
                finalSignature = Convert.ToBase64String(signatureBytes);
            }

            _logger.LogInformation("8. Final Signature: {Signature}", finalSignature);
            _logger.LogInformation("9. Header Value: HMACSHA256={Signature}", finalSignature);
            _logger.LogInformation("=== END DEBUG ===");

            try
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl);

                requestMessage.Headers.Add("Client-Id", clientId);
                requestMessage.Headers.Add("Request-Id", requestId);
                requestMessage.Headers.Add("Request-Timestamp", timestamp);
                requestMessage.Headers.Add("Signature", $"HMACSHA256={finalSignature}");

                requestMessage.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                _logger.LogInformation("Request Headers:");
                _logger.LogInformation("  Client-Id: {ClientId}", clientId);
                _logger.LogInformation("  Request-Id: {RequestId}", requestId);
                _logger.LogInformation("  Request-Timestamp: {Timestamp}", timestamp);
                _logger.LogInformation("  Signature: HMACSHA256={Signature}", finalSignature);
                _logger.LogInformation("  Content-Type: application/json");

                var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("Response Status: {StatusCode}", response.StatusCode);
                _logger.LogInformation("Response Headers: {Headers}",
                    string.Join(", ", response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));
                _logger.LogInformation("Response Body: {Body}", responseContent);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("DOKU API Error: {StatusCode} - {Content}", response.StatusCode, responseContent);

                    // Try to parse error response
                    try
                    {
                        var errorResponse = JsonConvert.DeserializeObject<dynamic>(responseContent);
                        var errorMessage = errorResponse?.error?.message?.ToString() ?? "Unknown error";
                        var errorCode = errorResponse?.error?.code?.ToString() ?? "unknown_error";

                        return new DokuPaymentResponse
                        {
                            IsSuccess = false,
                            ErrorMessage = $"DOKU Error [{errorCode}]: {errorMessage}"
                        };
                    }
                    catch
                    {
                        return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = responseContent };
                    }
                }

                try
                {
                    var result = JsonConvert.DeserializeObject<dynamic>(responseContent);

                    _logger.LogInformation("Response structure analysis:");
                    if (result != null)
                    {
                        var properties = ((Newtonsoft.Json.Linq.JObject)result).Properties().Select(p => p.Name);
                        _logger.LogInformation("Root properties: {Properties}", string.Join(", ", properties));
                    }

                    string? paymentUrl = null;

                    if (result?.response?.payment?.url != null)
                        paymentUrl = result.response.payment.url.ToString();
                    else if (result?.payment?.url != null)
                        paymentUrl = result.payment.url.ToString();
                    else if (result?.url != null)
                        paymentUrl = result.url.ToString();
                    else if (result?.data?.payment_url != null)
                        paymentUrl = result.data.payment_url.ToString();
                    else if (result?.checkout_url != null)
                        paymentUrl = result.checkout_url.ToString();

                    if (!string.IsNullOrEmpty(paymentUrl))
                    {
                        _logger.LogInformation("Payment URL found: {PaymentUrl}", paymentUrl);
                        return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
                    }

                    _logger.LogWarning("Payment URL not found in response. Full response: {Response}", responseContent);
                    return new DokuPaymentResponse
                    {
                        IsSuccess = false,
                        ErrorMessage = "URL pembayaran tidak ditemukan dalam respons. Response: " + responseContent
                    };
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "Error parsing DOKU response JSON: {Response}", responseContent);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Error parsing response dari DOKU." };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saat mengirim request ke DOKU");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = ex.Message };
            }
        }
    }
}