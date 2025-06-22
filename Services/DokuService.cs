using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            var apiUrl = dokuConfig["ApiUrl"];
            var callbackUrl = dokuConfig["CallbackUrl"]?.Trim();

            var requestPath = "/checkout/v1/payment";

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
                    line_items = request.LineItems.Select(item => new
                    {
                        name = item.Name,
                        price = item.Price,
                        quantity = item.Quantity
                    })
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
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new SnakeCaseNamingStrategy()
                },
                NullValueHandling = NullValueHandling.Ignore
            };

            var requestJson = JsonConvert.SerializeObject(payload, jsonSettings);

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
            _logger.LogInformation("Request Body (JSON): {RequestBody}", requestJson);

            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl + requestPath);
                httpRequest.Headers.Add("Client-Id", clientId);
                httpRequest.Headers.Add("Request-Id", requestId);
                httpRequest.Headers.Add("Request-Timestamp", requestTimestamp);
                httpRequest.Headers.Add("Digest", digest);
                httpRequest.Headers.Add("Signature", $"HMACSHA256={signature}");

                httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var response = await _httpClient.SendAsync(httpRequest);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gagal membuat pembayaran DOKU. Status: {StatusCode}, Body: {ResponseBody}", response.StatusCode, responseBody);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Gagal membuat pembayaran: {responseBody}" };
                }

                var dokuResponse = JsonConvert.DeserializeObject<dynamic>(responseBody);
                string? paymentUrl = dokuResponse?.response?.payment?.url ?? dokuResponse?.payment?.url ?? dokuResponse?.url ?? dokuResponse?.checkout_url ?? dokuResponse?.payment_url;

                if (!string.IsNullOrEmpty(paymentUrl))
                {
                    return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
                }

                _logger.LogError("Gagal mem-parsing URL pembayaran dari respons DOKU: {ResponseBody}", responseBody);
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Gagal mendapatkan URL pembayaran dari respons DOKU." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saat membuat pembayaran DOKU");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Terjadi kesalahan: {ex.Message}" };
            }
        }
    }
}