using System.Text.Json.Serialization;

namespace olx_be_api.Services
{
    public class DokuLineItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("price")]
        public int Price { get; set; }
    }

    public class DokuRequestPayload
    {
        [JsonPropertyName("order")]
        public DokuOrderRequest Order { get; set; } = new();

        [JsonPropertyName("payment")]
        public DokuPaymentDetails Payment { get; set; } = new();

        [JsonPropertyName("customer")]
        public DokuCustomerDetails Customer { get; set; } = new();
    }

    public class DokuOrderRequest
    {
        [JsonPropertyName("amount")]
        public int Amount { get; set; }

        [JsonPropertyName("invoice_number")]
        public string? InvoiceNumber { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "IDR";

        [JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; set; }

        [JsonPropertyName("line_items")]
        public List<DokuLineItem> LineItems { get; set; } = new();
    }

    public class DokuPaymentDetails
    {
        [JsonPropertyName("payment_due_date")]
        public int PaymentDueDate { get; set; } = 60;
    }

    public class DokuCustomerDetails
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }
    }

    public class DokuPaymentRequest
    {
        public string? InvoiceNumber { get; set; }
        public int Amount { get; set; }
        public string? ProductName { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerEmail { get; set; }
        public List<DokuLineItem> LineItems { get; set; } = new List<DokuLineItem>();
    }

    public class DokuPaymentResponse
    {
        public bool IsSuccess { get; set; }
        public string? PaymentUrl { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public interface IDokuService
    {
        Task<DokuPaymentResponse> CreatePayment(DokuPaymentRequest request);
    }
}