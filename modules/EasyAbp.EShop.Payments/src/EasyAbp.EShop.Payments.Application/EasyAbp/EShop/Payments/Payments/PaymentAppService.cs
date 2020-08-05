using EasyAbp.EShop.Orders.Orders;
using EasyAbp.EShop.Orders.Orders.Dtos;
using EasyAbp.EShop.Payments.Authorization;
using EasyAbp.EShop.Payments.Payments.Dtos;
using EasyAbp.EShop.Stores.Permissions;
using EasyAbp.PaymentService.Payments;
using Microsoft.AspNetCore.Authorization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Users;

namespace EasyAbp.EShop.Payments.Payments
{
    [Authorize]
    public class PaymentAppService : ReadOnlyAppService<Payment, PaymentDto, Guid, GetPaymentListDto>,
        IPaymentAppService
    {
        protected override string GetPolicyName { get; set; } = PaymentsPermissions.Payments.Default;
        protected override string GetListPolicyName { get; set; } = PaymentsPermissions.Payments.Default;

        private readonly IPayableChecker _payableChecker;
        private readonly IDistributedEventBus _distributedEventBus;
        private readonly IOrderAppService _orderAppService;

        public PaymentAppService(
            IPayableChecker payableChecker,
            IDistributedEventBus distributedEventBus,
            IOrderAppService orderAppService,
            IPaymentRepository repository) : base(repository)
        {
            _payableChecker = payableChecker;
            _distributedEventBus = distributedEventBus;
            _orderAppService = orderAppService;
        }

        public override async Task<PaymentDto> GetAsync(Guid id)
        {
            var payment = await base.GetAsync(id);

            await AuthorizationService.CheckAsync(GetPolicyName);

            if (payment.UserId != CurrentUser.GetId())
            {
                if (payment.StoreId.HasValue)
                {
                    if (await AuthorizationService.IsStoreOwnerGrantedAsync(payment.StoreId.Value,
                        PaymentsPermissions.Payments.Manage))
                    {
                        return payment;
                    }

                    await AuthorizationService.CheckAsync(PaymentsPermissions.Payments.CrossStore);
                }

                await AuthorizationService.CheckAsync(PaymentsPermissions.Payments.Manage);
            }

            return payment;
        }

        protected override IQueryable<Payment> CreateFilteredQuery(GetPaymentListDto input)
        {
            var query = base.CreateFilteredQuery(input);

            if (input.UserId.HasValue)
            {
                query = query.Where(x => x.UserId == input.UserId.Value);
            }

            return query;
        }

        public override async Task<PagedResultDto<PaymentDto>> GetListAsync(GetPaymentListDto input)
        {
            if (input.UserId != CurrentUser.GetId())
            {
                await AuthorizationService.CheckAsync(PaymentsPermissions.Payments.Manage);

                if (input.StoreId.HasValue)
                {
                    await AuthorizationService.CheckStoreOwnerAsync(input.StoreId.Value,
                            PaymentsPermissions.Payments.Manage);
                }
                else
                {
                    await AuthorizationService.CheckAsync(PaymentsPermissions.Payments.CrossStore);
                }
            }

            return await base.GetListAsync(input);
        }

        [Authorize(PaymentsPermissions.Payments.Create)]
        public virtual async Task CreateAsync(CreatePaymentDto input)
        {
            // Todo: should avoid duplicate creations. (concurrent lock)

            var orders = new List<OrderDto>();

            foreach (var orderId in input.OrderIds)
            {
                orders.Add(await _orderAppService.GetAsync(orderId));
            }

            var extraProperties = new Dictionary<string, object> { { "StoreId", orders.First().StoreId } };

            await _payableChecker.CheckAsync(input, orders, extraProperties);

            // Todo: should avoid duplicate creations.

            await _distributedEventBus.PublishAsync(new CreatePaymentEto
            {
                TenantId = CurrentTenant.Id,
                UserId = CurrentUser.GetId(),
                PaymentMethod = input.PaymentMethod,
                Currency = orders.First().Currency,
                ExtraProperties = new Dictionary<string, object>(),
                PaymentItems = orders.Select(order => new CreatePaymentItemEto
                {
                    ItemType = PaymentsConsts.PaymentItemType,
                    ItemKey = order.Id,
                    Currency = order.Currency,
                    OriginalPaymentAmount = order.TotalPrice,
                    ExtraProperties = new Dictionary<string, object> {{"StoreId", orders.First().StoreId.ToString()}}
                }).ToList()
            };

            await _payableChecker.CheckAsync(input, orders, createPaymentEto);
            
            await _distributedEventBus.PublishAsync(createPaymentEto);
        }
    }
}