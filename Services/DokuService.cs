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
                _logger.LogError("DokuSettings (ClientId, SecretKey, ApiUrl, atau CallbackUrl) tidak dikonfigurasi dengan benar.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Konfigurasi DOKU tidak lengkap." };
            }

            var requestId = Guid.NewGuid().ToString();
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            var httpMethod = "POST";

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
                payment = new { payment_due_date = 60 },
                customer = new { name = request.CustomerName, email = request.CustomerEmail }
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
            using var sha256 = SHA256.Create();
            var bodyHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(requestJson));
            var hexBodyHash = BitConverter.ToString(bodyHash).Replace("-", "").ToLowerInvariant();

            var stringToSign = $"{httpMethod}:{endpointPath}:{clientId}:{hexBodyHash}:{timestamp}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
            var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
            var finalSignature = Convert.ToBase64String(signatureBytes);

            _logger.LogInformation("Request JSON: {RequestJson}", requestJson);
            _logger.LogInformation("StringToSign: {StringToSign}", stringToSign);
            _logger.LogInformation("Signature: {Signature}", finalSignature);

            try
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl);
                requestMessage.Headers.Add("Client-Id", clientId);
                requestMessage.Headers.Add("Request-Id", requestId);
                requestMessage.Headers.Add("Request-Timestamp", timestamp);
                requestMessage.Headers.Add("Signature", $"HMACSHA256={finalSignature}");

                requestMessage.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("Response Status: {StatusCode}", response.StatusCode);
                _logger.LogInformation("Response Body: {Body}", responseContent);

                if (!response.IsSuccessStatusCode)
                {
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = responseContent };
                }

                var result = JsonConvert.DeserializeObject<dynamic>(responseContent);
                string? paymentUrl = result?.response?.payment?.url ?? result?.payment?.url ?? result?.url;

                if (!string.IsNullOrEmpty(paymentUrl))
                {
                    return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
                }

                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "URL pembayaran tidak ditemukan dalam respons." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saat mengirim request ke DOKU");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = ex.Message };
            }
        }
    }
}
