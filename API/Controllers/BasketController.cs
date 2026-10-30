using API.Dtos;
using AutoMapper;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers
{
    public class BasketController: BaseApiController
    {
        private readonly IBasketRepository _basketRepository;
        private readonly IMapper _mapper;
        public BasketController(IBasketRepository basketRepository, IMapper mapper)
        {
            _basketRepository = basketRepository;
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<ActionResult<CustomerBasket>> GetBasketById(string id)
        {
            var basket = await _basketRepository.GetBasketAsync(id);
            return Ok(basket ?? new CustomerBasket(id));
        }

        [HttpPost]
        public async Task<ActionResult<CustomerBasket>> UpdateBasket(CustomerBasketDto basket)
        {
            var customerBasket = _mapper.Map<CustomerBasketDto, CustomerBasket>(basket);

            // US-0012 AC-4: el subtotal elegible se calcula solo con productos en USD, antes de envío/impuestos/cargos/otros descuentos.
            var productSubtotal = customerBasket.Items.Sum(item => item.Price * item.Quantity);

            // US-0012 AC-1/AC-2/AC-3/AC-5: se recalcula la elegibilidad en cada actualización y solo se activa si el subtotal es estrictamente mayor a 500.00 USD.
            var isDiscountEligible = productSubtotal > 500m;
            var automaticDiscount = isDiscountEligible
                ? decimal.Round(productSubtotal * 0.05m, 2, System.MidpointRounding.AwayFromZero)
                : 0m;

            _ = automaticDiscount;

            var updatedBasket = await _basketRepository.UpdateBasketAsync(customerBasket);
            return Ok(updatedBasket);
        }

        [HttpDelete]
        public async Task DeleteBasket(string id)
        {
            await _basketRepository.DeleteBasketAsync(id);
        }
    }
}