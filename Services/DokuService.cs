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
        private readonly JsonSerializerOptions _jsonOptions;

        public DokuService(HttpClient httpClient, IConfiguration configuration, ILogger<DokuService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        public async Task<DokuPaymentResponse> CreatePayment(DokuPaymentRequest request)
        {
            var dokuConfig = _configuration.GetSection("DokuSettings");
            var clientId = dokuConfig["ClientId"]?.Trim();
            var secretKey = dokuConfig["SecretKey"]?.Trim();
            var apiBaseUrl = dokuConfig["ApiUrl"]?.TrimEnd('/');
            var callbackUrl = dokuConfig["CallbackUrl"]?.Trim();

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secretKey) ||
                string.IsNullOrEmpty(apiBaseUrl) || string.IsNullOrEmpty(callbackUrl))
            {
                _logger.LogError("Konfigurasi DOKU tidak lengkap.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Konfigurasi DOKU tidak lengkap." };
            }

            var requestId = Guid.NewGuid().ToString();
            var requestTimestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var requestPath = "/checkout/v1/payment";
            var fullUrl = apiBaseUrl + requestPath;

            var payload = new DokuRequestPayload
            {
                Order = new DokuOrderRequest
                {
                    Amount = request.Amount,
                    InvoiceNumber = request.InvoiceNumber,
                    Currency = "IDR",
                    CallbackUrl = callbackUrl,
                    LineItems = request.LineItems
                },
                Payment = new DokuPaymentDetails
                {
                    PaymentDueDate = 60
                },
                Customer = new DokuCustomerDetails
                {
                    Name = request.CustomerName,
                    Email = request.CustomerEmail
                }
            };

            string requestJson;
            try
            {
                requestJson = JsonSerializer.Serialize(payload, _jsonOptions);
                _logger.LogInformation("FINAL JSON sebelum dikirim ke DOKU:\n{0}", requestJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gagal serialize JSON payload.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = ex.Message };
            }

            string digestValue;
            using (var sha256 = SHA256.Create())
            {
                digestValue = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(requestJson)));
            }
            string digestHeader = $"SHA-256={digestValue}";

            var stringToSign = $"Client-Id:{clientId}\n" +
                               $"Request-Id:{requestId}\n" +
                               $"Request-Timestamp:{requestTimestamp}\n" +
                               $"Request-Target:{requestPath}\n" +
                               $"Digest:{digestHeader}";

            _logger.LogInformation("StringToSign:\n{0}", stringToSign);

            string signature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey)))
            {
                signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
            }

            string signatureHeader =
                $"Client-Id=\"{clientId}\",Request-Id=\"{requestId}\",Request-Timestamp=\"{requestTimestamp}\"," +
                $"Request-Target=\"{requestPath}\",Digest=\"{digestHeader}\",Signature=\"{signature}\"";

            _logger.LogInformation("Digest Header: {0}", digestHeader);
            _logger.LogInformation("Signature Header: {0}", signatureHeader);

            try
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl);
                requestMessage.Headers.Add("Client-Id", clientId);
                requestMessage.Headers.Add("Request-Id", requestId);
                requestMessage.Headers.Add("Request-Timestamp", requestTimestamp);
                requestMessage.Headers.Add("Digest", digestHeader);
                requestMessage.Headers.Add("Signature", signatureHeader);
                requestMessage.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(requestMessage);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Gagal membuat pembayaran DOKU. Status: {0}, Body: {1}", response.StatusCode, responseBody);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Error DOKU: {responseBody}" };
                }

                using var responseDoc = JsonDocument.Parse(responseBody);
                var paymentUrl = responseDoc.RootElement.GetProperty("response")
                                    .GetProperty("payment").GetProperty("url").GetString();

                if (string.IsNullOrEmpty(paymentUrl))
                {
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Gagal mendapatkan URL pembayaran dari respons DOKU." };
                }

                return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Terjadi kesalahan saat membuat pembayaran DOKU.");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Terjadi kesalahan sistem: {ex.Message}" };
            }
        }
    }
}