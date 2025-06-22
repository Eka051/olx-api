using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            var apiUrl = dokuConfig["ApiUrl"];
            var callbackUrl = dokuConfig["CallbackUrl"]?.Trim();

            var environment = dokuConfig["Environment"] ?? "sandbox";

            var requestPath = environment.ToLower() == "production"
                ? "/checkout/v1/payment"
                : "/checkout/v1/payment";

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(callbackUrl))
            {
                _logger.LogError("DokuSettings (ClientId, SecretKey, ApiUrl, atau CallbackUrl) tidak dikonfigurasi dengan benar.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Konfigurasi DOKU tidak lengkap." };
            }

            var requestId = Guid.NewGuid().ToString();
            var requestTimestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"); 

            var payload = new
            {
                order = new
                {
                    amount = request.Amount,
                    invoice_number = request.InvoiceNumber,
                    currency = "IDR",
                    callback_url = callbackUrl,
                    line_items = request.LineItems,
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

            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            };

            var requestJson = JsonSerializer.Serialize(payload, options);

            var requestBodyBytes = Encoding.UTF8.GetBytes(requestJson);
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(requestBodyBytes);
            var digest = "SHA-256=" + Convert.ToBase64String(hashBytes);

            var signatureComponent = $"Client-Id:{clientId}\n" +
                                   $"Request-Id:{requestId}\n" +
                                   $"Request-Timestamp:{requestTimestamp}\n" +
                                   $"Request-Target:{requestPath}\n" +
                                   $"Digest:{digest}";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureComponent)));

            _logger.LogInformation("--- DOKU Payment Creation ---");
            _logger.LogInformation("Environment: {Environment}", environment);
            _logger.LogInformation("Client-Id: {ClientId}", clientId);
            _logger.LogInformation("Request-Id: {RequestId}", requestId);
            _logger.LogInformation("Request-Timestamp: {RequestTimestamp}", requestTimestamp);
            _logger.LogInformation("Request-Target: {RequestTarget}", requestPath);
            _logger.LogInformation("Request Body (JSON): {RequestBody}", requestJson);
            _logger.LogInformation("Digest: {Digest}", digest);
            _logger.LogInformation("String-to-Sign: {StringToSign}", signatureComponent.Replace("\n", "\\n"));
            _logger.LogInformation("Signature: {Signature}", signature);

            var fullRequestUrl = apiUrl.TrimEnd('/') + requestPath;
            _logger.LogInformation("Full Request URL: {FullRequestUrl}", fullRequestUrl);

            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullRequestUrl);

                httpRequest.Headers.Add("Client-Id", clientId);
                httpRequest.Headers.Add("Request-Id", requestId);
                httpRequest.Headers.Add("Request-Timestamp", requestTimestamp);
                httpRequest.Headers.Add("Request-Target", requestPath);
                httpRequest.Headers.Add("Digest", digest);
                httpRequest.Headers.Add("Signature", $"HMACSHA256={signature}");

                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                _logger.LogInformation("Sending request to DOKU...");

                var response = await _httpClient.SendAsync(httpRequest);
                var responseBody = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("DOKU Response Status: {StatusCode}", response.StatusCode);
                _logger.LogInformation("DOKU Response Body: {ResponseBody}", responseBody);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gagal membuat pembayaran DOKU. Status: {StatusCode}, Body: {ResponseBody}", response.StatusCode, responseBody);

                    // Berikan pesan error yang lebih spesifik
                    var errorMessage = response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.NotFound => "Endpoint tidak ditemukan (404). Periksa URL API dan path endpoint.",
                        System.Net.HttpStatusCode.Unauthorized => "Tidak terotorisasi (401). Periksa Client-Id dan Secret Key.",
                        System.Net.HttpStatusCode.BadRequest => "Request tidak valid (400). Periksa format payload.",
                        System.Net.HttpStatusCode.Forbidden => "Akses ditolak (403). Periksa signature dan permissions.",
                        _ => $"Error {(int)response.StatusCode}: {responseBody}"
                    };

                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = errorMessage };
                }

                _logger.LogInformation("Berhasil membuat pembayaran DOKU. Response: {ResponseBody}", responseBody);

                try
                {
                    var dokuResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);

                    string? paymentUrl = null;

                    // Struktur 1: response.payment.url
                    if (dokuResponse.TryGetProperty("response", out var responseElement) &&
                        responseElement.TryGetProperty("payment", out var paymentElement) &&
                        paymentElement.TryGetProperty("url", out var urlElement))
                    {
                        paymentUrl = urlElement.GetString();
                    }
                    // Struktur 2: payment.url
                    else if (dokuResponse.TryGetProperty("payment", out var directPaymentElement) &&
                             directPaymentElement.TryGetProperty("url", out var directUrlElement))
                    {
                        paymentUrl = directUrlElement.GetString();
                    }
                    // Struktur 3: url langsung
                    else if (dokuResponse.TryGetProperty("url", out var directUrl))
                    {
                        paymentUrl = directUrl.GetString();
                    }
                    // Struktur 4: checkout_url atau payment_url
                    else if (dokuResponse.TryGetProperty("checkout_url", out var checkoutUrl))
                    {
                        paymentUrl = checkoutUrl.GetString();
                    }
                    else if (dokuResponse.TryGetProperty("payment_url", out var payUrl))
                    {
                        paymentUrl = payUrl.GetString();
                    }

                    if (!string.IsNullOrEmpty(paymentUrl))
                    {
                        return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
                    }

                    _logger.LogError("Gagal mem-parsing URL pembayaran dari respons DOKU: {ResponseBody}", responseBody);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Gagal mendapatkan URL pembayaran dari respons DOKU." };
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error JSON saat mem-parsing respons DOKU. Body: {ResponseBody}", responseBody);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Format respons dari DOKU tidak valid." };
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP Request Exception saat menghubungi DOKU API");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Gagal menghubungi DOKU API: {ex.Message}" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saat membuat pembayaran DOKU");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Terjadi kesalahan: {ex.Message}" };
            }
        }
    }
}