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

            // Pastikan payload sesuai dengan format yang diharapkan DOKU
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
                    }).ToArray()
                },
                payment = new { payment_due_date = 60 },
                customer = new { name = request.CustomerName, email = request.CustomerEmail }
            };

            // Konfigurasi JSON serialization yang konsisten
            var jsonSettings = new JsonSerializerSettings
            {
                ContractResolver = new DefaultContractResolver
                {
                    NamingStrategy = new SnakeCaseNamingStrategy()
                },
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None // Pastikan tidak ada whitespace
            };

            // Serialize payload ke JSON minified
            var requestJson = JsonConvert.SerializeObject(payload, jsonSettings);

            _logger.LogInformation("Minified JSON: {RequestJson}", requestJson);

            // Hitung SHA256 hash dari JSON body
            string hexBodyHash;
            using (var sha256 = SHA256.Create())
            {
                var bodyBytes = Encoding.UTF8.GetBytes(requestJson);
                var hashBytes = sha256.ComputeHash(bodyBytes);
                hexBodyHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }

            _logger.LogInformation("SHA256 Hash (hex lowercase): {Hash}", hexBodyHash);

            // Buat string yang akan di-sign sesuai format DOKU
            var stringToSign = $"{httpMethod}:{endpointPath}:{clientId}:{hexBodyHash}:{timestamp}";

            _logger.LogInformation("StringToSign: {StringToSign}", stringToSign);

            // Generate signature menggunakan HMAC-SHA256
            string finalSignature;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey)))
            {
                var stringToSignBytes = Encoding.UTF8.GetBytes(stringToSign);
                var signatureBytes = hmac.ComputeHash(stringToSignBytes);
                finalSignature = Convert.ToBase64String(signatureBytes);
            }

            _logger.LogInformation("Final Signature: {Signature}", finalSignature);

            try
            {
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, fullUrl);

                // Set headers sesuai dokumentasi DOKU
                requestMessage.Headers.Add("Client-Id", clientId);
                requestMessage.Headers.Add("Request-Id", requestId);
                requestMessage.Headers.Add("Request-Timestamp", timestamp);
                requestMessage.Headers.Add("Signature", $"HMACSHA256={finalSignature}");

                // Set content dengan encoding yang tepat
                requestMessage.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                _logger.LogInformation("Sending request to: {Url}", fullUrl);
                _logger.LogInformation("Headers: Client-Id={ClientId}, Request-Id={RequestId}, Request-Timestamp={Timestamp}, Signature=HMACSHA256={Signature}",
                    clientId, requestId, timestamp, finalSignature);

                var response = await _httpClient.SendAsync(requestMessage);
                var responseContent = await response.Content.ReadAsStringAsync();

                _logger.LogInformation("Response Status: {StatusCode}", response.StatusCode);
                _logger.LogInformation("Response Body: {Body}", responseContent);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("DOKU API Error: {StatusCode} - {Content}", response.StatusCode, responseContent);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = responseContent };
                }

                // Parse response untuk mendapatkan payment URL
                try
                {
                    var result = JsonConvert.DeserializeObject<dynamic>(responseContent);

                    // Coba berbagai kemungkinan path untuk payment URL
                    string? paymentUrl = null;

                    if (result?.response?.payment?.url != null)
                        paymentUrl = result.response.payment.url;
                    else if (result?.payment?.url != null)
                        paymentUrl = result.payment.url;
                    else if (result?.url != null)
                        paymentUrl = result.url;
                    else if (result?.data?.payment_url != null)
                        paymentUrl = result.data.payment_url;

                    if (!string.IsNullOrEmpty(paymentUrl))
                    {
                        _logger.LogInformation("Payment URL found: {PaymentUrl}", paymentUrl);
                        return new DokuPaymentResponse { IsSuccess = true, PaymentUrl = paymentUrl };
                    }

                    _logger.LogWarning("Payment URL not found in response. Full response: {Response}", responseContent);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "URL pembayaran tidak ditemukan dalam respons." };
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "Error parsing DOKU response JSON: {Response}", responseContent);
                    return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Error parsing response dari DOKU." };
                }
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP error saat mengirim request ke DOKU");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"HTTP Error: {httpEx.Message}" };
            }
            catch (TaskCanceledException tcEx)
            {
                _logger.LogError(tcEx, "Timeout saat mengirim request ke DOKU");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = "Request timeout ke DOKU API." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saat mengirim request ke DOKU");
                return new DokuPaymentResponse { IsSuccess = false, ErrorMessage = $"Unexpected error: {ex.Message}" };
            }
        }
    }
}