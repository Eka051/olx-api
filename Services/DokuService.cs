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
            var callbackUrl = dokuConfig["CallbackUrl"];
            var requestPath = "/checkout/v1/payment";

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(callbackUrl))
            {
                _logger.LogError("DokuSettings (ClientId, SecretKey, atau CallbackUrl) tidak dikonfigurasi dengan benar.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Konfigurasi DOKU tidak lengkap." };
            }

            var requestId = Guid.NewGuid().ToString();
            var requestTimestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

            var payload = new
            {
                order = new
                {
                    amount = request.Amount,
                    invoice_number = request.InvoiceNumber,
                    currency = "IDR",
                    callback_url = callbackUrl,
                    line_items = request.LineItems.Select(item => new
                    {
                        name = item.Name,
                        price = item.Price,
                        quantity = item.Quantity
                    }).ToList()
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

            var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            var requestJson = JsonSerializer.Serialize(payload, options);
            var digest = Convert.ToBase64String(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(requestJson)));

            var signatureComponent = $"Client-Id:{clientId}\nRequest-Id:{requestId}\nRequest-Timestamp:{requestTimestamp}\nRequest-Target:{requestPath}\nDigest:{digest}";

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signatureComponent)));

            _logger.LogInformation("--- DOKU Payment Creation ---");
            _logger.LogInformation("Request-Id: {RequestId}", requestId);
            _logger.LogInformation("Request-Timestamp: {RequestTimestamp}", requestTimestamp);
            _logger.LogInformation("Request-Target: {RequestTarget}", requestPath);
            _logger.LogInformation("Request Body (JSON): {RequestBody}", requestJson);
            _logger.LogInformation("Digest: {Digest}", digest);
            _logger.LogInformation("String-to-Sign: {StringToSign}", signatureComponent.Replace("\n", "\\n"));
            _logger.LogInformation("Final Signature (HMACSHA256): {Signature}", signature);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestPath);
            httpRequest.Headers.Add("Client-Id", clientId);
            httpRequest.Headers.Add("Request-Id", requestId);
            httpRequest.Headers.Add("Request-Timestamp", requestTimestamp);
            httpRequest.Headers.Add("Signature", $"HMACSHA256={signature}");
            httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Gagal membuat pembayaran DOKU. Status: {StatusCode}, Body: {ResponseBody}", response.StatusCode, responseBody);
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Gagal membuat pembayaran: {response.StatusCode} - {responseBody}" };
            }

            _logger.LogInformation("Berhasil membuat pembayaran DOKU. Response: {ResponseBody}", responseBody);

            try
            {
                var dokuResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);
                if (dokuResponse.TryGetProperty("response", out var responseElement) &&
                    responseElement.TryGetProperty("payment", out var paymentElement) &&
                    paymentElement.TryGetProperty("url", out var urlElement) &&
                    urlElement.GetString() is { } paymentUrl)
                {
                    return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
                }

                _logger.LogError("Gagal mem-parsing URL pembayaran dari respons DOKU: {ResponseBody}", responseBody);
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Gagal mendapatkan URL pembayaran dari respons DOKU." };
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error JSON saat mem-parsing respons DOKU. Body: {ResponseBody}", responseBody);
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Format respons dari DOKU tidak valid." };
            }
        }
    }
}
