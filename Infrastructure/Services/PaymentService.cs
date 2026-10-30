using Core.Entities;
using Core.Entities.OrderAggregate;
using Core.Interfaces;
using Core.Specifications;
using Microsoft.Extensions.Configuration;
using Stripe;
using Product = Core.Entities.Product;

namespace Infrastructure.Services
{
    public class PaymentService : IPaymentService
    {
        private readonly IBasketRepository _basketRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IConfiguration _config;
        public PaymentService(IBasketRepository basketRepository, IUnitOfWork unitOfWork, IConfiguration config) 
        {
            _basketRepository = basketRepository;
            _unitOfWork = unitOfWork;
            _config = config;
        }
        public async Task<CustomerBasket> CreateOrUpdatePaymentIntent(string basketId)
        {
            StripeConfiguration.ApiKey = _config["StripeSettings:SecretKey"];

            var basket = await _basketRepository.GetBasketAsync(basketId);

            if (basket == null) return null;

            var shippingPrice = 0m;

            if (basket.DeliveryMethodId.HasValue)
            {
                var deliveryMethod = await _unitOfWork.Repository<DeliveryMethod>().GetByIdAsync((int)basket.DeliveryMethodId);

                // US-0010 AC-3: If the selected delivery method no longer exists, stop the charge and ask the customer to review the summary.
                if (deliveryMethod == null) return null;

                shippingPrice = deliveryMethod.Price;
            }

            var basketChanged = false;

            foreach (var item in basket.Items)
            {
                var productItem = await _unitOfWork.Repository<Product>().GetByIdAsync(item.Id);

                // US-0010 AC-3: If a product no longer exists, prevent payment because the current basket total cannot be trusted.
                if (productItem == null) return null;

                if (item.Price != productItem.Price)
                {
                    item.Price = productItem.Price;
                    basketChanged = true;
                }
            }

            basket.ShippingPrice = shippingPrice;

            if (basketChanged)
            {
                await _basketRepository.UpdateBasketAsync(basket);

                // US-0010 AC-3: Product prices changed since the checkout summary was shown, so require a review before charging.
                return null;
            }

            // US-0010 AC-1: Recalculate the payable total from the authoritative basket values used by checkout.
            var subtotal = basket.Items.Sum(item => item.Price * item.Quantity);
            var discount = subtotal > 500m ? Math.Round(subtotal * 0.05m, 2, MidpointRounding.AwayFromZero) : 0m;
            var totalFinal = subtotal - discount + shippingPrice;

            // US-0010 AC-4: Prevent payment if the final total is inconsistent before sending any amount to Stripe.
            if (totalFinal < 0) return null;

            // US-0010 AC-2: Stripe receives the final total including the automatic discount and shipping, expressed in cents.
            var amount = (long)Math.Round(totalFinal * 100, MidpointRounding.AwayFromZero);

            // Stripe service

            var service = new PaymentIntentService();

            PaymentIntent intent;

            if (string.IsNullOrEmpty(basket.PaymentIntentId))
            {
                var options = new PaymentIntentCreateOptions
                {
                    Amount = amount,
                    Currency = "usd",
                    PaymentMethodTypes = new List<string> { "card" }
                };
                intent = await service.CreateAsync(options);
                basket.PaymentIntentId = intent.Id;
                basket.ClientSecret = intent.ClientSecret;
            }
            else
            {
                var options = new PaymentIntentUpdateOptions
                {
                    Amount = amount
                };
                await service.UpdateAsync(basket.PaymentIntentId, options);
            }

            await _basketRepository.UpdateBasketAsync(basket);

            return basket;
        }

        public async Task<Order> UpdateOrderPaymentFailed(string paymentIntentId)
        {
            var spec = new OrderByPaymentIntentIdSpecification(paymentIntentId);
            var order = await _unitOfWork.Repository<Order>().GetEntityWithSpec(spec);

            if (order == null) return null;

            order.Status = OrderStatus.PaymentFailed;
            await _unitOfWork.Complete();

            return order;
        }

        public async Task<Order> UpdateOrderPaymentSucceeded(string paymentIntentId)
        {
            var spec = new OrderByPaymentIntentIdSpecification(paymentIntentId);
            var order = await _unitOfWork.Repository<Order>().GetEntityWithSpec(spec);

            if (order == null) return null;

            order.Status = OrderStatus.PaymentReceived;
            await _unitOfWork.Complete();

            return order;
        }
    }
}