using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using olx_be_api.Data;
using olx_be_api.Helpers;
using olx_be_api.Models;
using olx_be_api.Models.Enums;
using olx_be_api.Services;
using System.Text.Json;

namespace olx_be_api.Controllers
{
    [Route("api/payments")]
    [ApiController]
    public class PaymentController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMidtransService _midtransService;

        public PaymentController(AppDbContext context, IMidtransService midtransService)
        {
            _context = context;
            _midtransService = midtransService;
        }

        [HttpPost("premium-subscriptions/{id}/checkout")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CheckoutPremiumAsync(int id)
        {
            var userId = User.GetUserId();
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return Unauthorized(new ApiErrorResponse { message = "Pengguna tidak ditemukan." });
            }

            var package = await _context.PremiumPackages.FindAsync(id);
            if (package == null || !package.IsActive)
            {
                return NotFound(new ApiErrorResponse { success = false, message = "Paket Premium tidak ditemukan" });
            }

            var transactionDetails = new List<TransactionItemDetail>
            {
                new TransactionItemDetail
                {
                    AdPackageId = 0,  
                    ProductId = 0, 
                    Price = package.Price,
                    Quantity = 1
                }
            };

            var transaction = new Transaction
            {
                UserId = userId,
                InvoiceNumber = $"INV-PREMIUM-{DateTime.UtcNow.Ticks}",
                Amount = package.Price,
                Status = TransactionStatus.Pending,
                Type = TransactionType.PremiumSubscription,
                ReferenceId = package.Id.ToString(),
                Details = JsonSerializer.Serialize(transactionDetails)
            };

            var midtransRequest = new MidtransRequest
            {
                InvoiceNumber = transaction.InvoiceNumber,
                Amount = transaction.Amount,
                CustomerDetails = new CustomerDetails
                {
                    FirstName = user.Name ?? "Pengguna OLX",
                    Email = user.Email!
                },
                ItemDetails = new List<ItemDetails>
                {
                    new ItemDetails
                    {
                        Id = package.Id.ToString(),
                        Name = package.Description ?? $"Premium {package.DurationDays} Hari",
                        Price = package.Price,
                        Quantity = 1
                    }
                }
            };

            var midtransResponse = await _midtransService.CreateSnapTransaction(midtransRequest);
            if (!midtransResponse.IsSuccess)
            {
                return StatusCode(500, new ApiErrorResponse { message = midtransResponse.ErrorMessage! });
            }

            transaction.PaymentUrl = midtransResponse.RedirectUrl;
            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();

            return CreatedAtAction(
                nameof(GetPaymentByInvoice),
                new { invoiceNumber = transaction.InvoiceNumber },
                new ApiResponse<string>
                {
                    success = true,
                    message = "URL pembayaran berhasil dibuat",
                    data = midtransResponse.RedirectUrl
                }
            );
        }

        [HttpPost("cart/checkout")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CheckoutCartAsync()
        {
            var userId = User.GetUserId();
            var user = await _context.Users.FindAsync(userId);
            if (user == null)
            {
                return Unauthorized(new ApiErrorResponse { message = "Pengguna tidak ditemukan." });
            }

            var cartItems = await _context.CartItems
                .Where(ci => ci.UserId == userId)
                .Include(ci => ci.AdPackage)
                .Include(ci => ci.Product)
                .ToListAsync();

            if (!cartItems.Any())
            {
                return NotFound(new ApiErrorResponse { success = false, message = "Keranjang Anda kosong." });
            }
            int totalAmount = cartItems.Sum(item => item.AdPackage.Price * item.Quantity);

            var itemDetails = cartItems.Select(item => new ItemDetails
            {
                Id = item.AdPackageId.ToString(),
                Name = $"Iklan '{item.AdPackage.Name}' untuk '{item.Product.Title}'",
                Price = item.AdPackage.Price,
                Quantity = item.Quantity
            }).ToList();

            var transactionDetails = cartItems.Select(item => new TransactionItemDetail
            {
                AdPackageId = item.AdPackageId,
                ProductId = item.ProductId,
                Price = item.AdPackage.Price,
                Quantity = item.Quantity
            }).ToList();

            var transaction = new Transaction
            {
                UserId = userId,
                InvoiceNumber = $"INV-CART-{DateTime.UtcNow.Ticks}",
                Amount = totalAmount,
                Status = TransactionStatus.Pending,
                Type = TransactionType.AdPackagePurchase,
                ReferenceId = Guid.NewGuid().ToString(),
                Details = JsonSerializer.Serialize(transactionDetails)
            };

            var midtransRequest = new MidtransRequest
            {
                InvoiceNumber = transaction.InvoiceNumber,
                Amount = totalAmount,
                CustomerDetails = new CustomerDetails
                {
                    FirstName = user.Name ?? "Pengguna OLX",
                    Email = user.Email!
                },
                ItemDetails = itemDetails
            };

            var midtransResponse = await _midtransService.CreateSnapTransaction(midtransRequest);
            if (!midtransResponse.IsSuccess)
            {
                return StatusCode(500, new ApiErrorResponse { message = midtransResponse.ErrorMessage! });
            }

            transaction.PaymentUrl = midtransResponse.RedirectUrl;
            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();

            _context.CartItems.RemoveRange(cartItems);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetPaymentByInvoice), new { invoiceNumber = transaction.InvoiceNumber },
                new ApiResponse<string> { success = true, message = "URL pembayaran berhasil dibuat", data = midtransResponse.RedirectUrl });
        }

        [HttpPost("webhooks/midtrans")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> MidtransNotification()
        {
            using var reader = new StreamReader(Request.Body);
            var requestBody = await reader.ReadToEndAsync();
            var notification = JsonSerializer.Deserialize<JsonElement>(requestBody); var transactionStatus = notification.GetProperty("transaction_status").GetString();
            var orderId = notification.GetProperty("order_id").GetString(); var transaction = await _context.Transactions
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.InvoiceNumber == orderId);

            if (transaction != null && transaction.Status == TransactionStatus.Pending)
            {
                if (transactionStatus == "settlement" || transactionStatus == "capture")
                {
                    transaction.Status = TransactionStatus.Success;
                    transaction.PaidAt = DateTime.UtcNow;

                    if (transaction.Type == TransactionType.PremiumSubscription)
                    {
                        var package = await _context.PremiumPackages.FindAsync(int.Parse(transaction.ReferenceId!));
                        if (package != null)
                        {
                            var user = transaction.User;
                            user.ProfileType = ProfileType.Premium;
                            var newExpiry = (user.PremiumUntil.HasValue && user.PremiumUntil > DateTime.UtcNow)
                                ? user.PremiumUntil.Value.AddDays(package.DurationDays)
                                : DateTime.UtcNow.AddDays(package.DurationDays);
                            user.PremiumUntil = newExpiry;
                            _context.Users.Update(user);
                        }
                    }
                    else if (transaction.Type == TransactionType.AdPackagePurchase && !string.IsNullOrEmpty(transaction.Details))
                    {
                        var purchasedItems = JsonSerializer.Deserialize<List<TransactionItemDetail>>(transaction.Details);
                        if (purchasedItems != null)
                        {
                            var adPackageIds = purchasedItems.Select(p => p.AdPackageId).Distinct().ToList();
                            var adPackages = await _context.AdPackages
                                .Include(ap => ap.Features)
                                .Where(ap => adPackageIds.Contains(ap.Id))
                                .ToDictionaryAsync(ap => ap.Id);

                            var allProductIds = purchasedItems.Select(item => item.ProductId).ToList();
                            var activeFeaturesForProducts = await _context.ActiveProductFeatures
                                .Where(af => allProductIds.Contains(af.ProductId))
                                .ToListAsync();

                            var newFeaturesToAdd = new List<ActiveProductFeature>();

                            foreach (var item in purchasedItems)
                            {
                                if (adPackages.TryGetValue(item.AdPackageId, out var adPackage))
                                {
                                    foreach (var feature in adPackage.Features)
                                    {
                                        var existingFeature = activeFeaturesForProducts.FirstOrDefault(af =>
                                            af.ProductId == item.ProductId && af.FeatureType == feature.FeatureType);

                                        if (existingFeature != null)
                                        {
                                            if (feature.FeatureType == AdFeatureType.Highlight || feature.FeatureType == AdFeatureType.Spotlight)
                                            {
                                                existingFeature.ExpiryDate = (existingFeature.ExpiryDate.HasValue && existingFeature.ExpiryDate > DateTime.UtcNow)
                                                    ? existingFeature.ExpiryDate.Value.AddDays(feature.DurationDays)
                                                    : DateTime.UtcNow.AddDays(feature.DurationDays);
                                            }
                                            else if (feature.FeatureType == AdFeatureType.Sundul)
                                            {
                                                existingFeature.RemainingQuantity += feature.Quantity;
                                            }
                                        }
                                        else
                                        {
                                            var newActiveFeature = new ActiveProductFeature
                                            {
                                                ProductId = item.ProductId,
                                                FeatureType = feature.FeatureType
                                            };

                                            if (feature.FeatureType == AdFeatureType.Highlight || feature.FeatureType == AdFeatureType.Spotlight)
                                            {
                                                newActiveFeature.ExpiryDate = DateTime.UtcNow.AddDays(feature.DurationDays);
                                            }
                                            else if (feature.FeatureType == AdFeatureType.Sundul)
                                            {
                                                newActiveFeature.RemainingQuantity = feature.Quantity;
                                            }
                                            newFeaturesToAdd.Add(newActiveFeature);
                                        }
                                    }
                                }
                            }

                            if (newFeaturesToAdd.Any())
                            {
                                await _context.ActiveProductFeatures.AddRangeAsync(newFeaturesToAdd);
                            }
                        }
                    }
                }
                else
                {
                    transaction.Status = TransactionStatus.Failed;
                }

                _context.Transactions.Update(transaction);
                await _context.SaveChangesAsync();
            }

            return Ok(new { success = true, message = "Notification received" });
        }

        [HttpGet("{invoiceNumber}")]
        [Authorize]
        [ProducesResponseType(typeof(ApiResponse<Transaction>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetPaymentByInvoice(string invoiceNumber)
        {
            var userId = User.GetUserId();
            var transaction = await _context.Transactions
                .FirstOrDefaultAsync(t => t.InvoiceNumber == invoiceNumber && t.UserId == userId);

            if (transaction == null)
            {
                return NotFound(new ApiErrorResponse
                {
                    success = false,
                    message = "Transaksi tidak ditemukan"
                });
            }

            return Ok(new ApiResponse<Transaction>
            {
                success = true,
                message = "Transaksi berhasil ditemukan",
                data = transaction
            });
        }

        [HttpGet("midtrans/config")]
        [AllowAnonymous]
        [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiErrorResponse), StatusCodes.Status500InternalServerError)]
        public IActionResult GetMidtransConfig()
        {
            var config = _midtransService.GetConfig();
            return Ok(new ApiResponse<object>
            {
                success = true,
                message = "Midtrans configuration retrieved",
                data = new
                {
                    clientKey = config.ClientKey,
                    isProduction = config.IsProduction,
                    snapUrl = config.IsProduction
                        ? "https://app.midtrans.com/snap/snap.js"
                        : "https://app.sandbox.midtrans.com/snap/snap.js"
                }
            });
        }
    }
}
