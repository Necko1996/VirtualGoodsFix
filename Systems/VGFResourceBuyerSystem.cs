using System;
using System.Threading;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Debug;
using Game.Economy;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Reflection;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace VirtualGoodsFix.Systems
{
    public partial class VGFResourceBuyerSystem : GameSystemBase
    {
        [Flags]
        private enum SaleFlags : byte
        {
            None = 0,
            CommercialSeller = 1,
            ImportFromOC = 2,
            Virtual = 4
        }

        private enum BuyerOutcome
        {
            CitizenSearchStarted,
            CitizenSuccess,
            CitizenInsufficientStock,
            CitizenCantAfford,
            CitizenPathfindFailed,
            CitizenStaleDestination,
            CitizenAtOutsideConnection,
            CompanySearchStarted,
            CompanySuccess,
            CompanyInsufficientStock,
            CompanyPathfindFailed,
            CompanyStaleDestination,
            Count
        }

        private enum FailedShoppingOrigin
        {
            NoHome,
            NoCurrentBuilding,
            AtHome,
            AtWorkplace,
            AtOutsideConnection,
            AtOther,
            Count
        }

        private struct SalesEvent
        {
            public SaleFlags m_Flags;

            public Entity m_Buyer;

            public Entity m_Seller;

            public Resource m_Resource;

            public int m_Amount;

            public float m_Distance;
        }

        [BurstCompile]
        private struct BuyJob : IJob
        {
            public NativeQueue<SalesEvent> m_SalesQueue;

            public BufferLookup<Game.Economy.Resources> m_Resources;

            public ComponentLookup<ServiceAvailable> m_Services;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<Household> m_Households;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<BuyingCompany> m_BuyingCompanies;

            [ReadOnly]
            public ComponentLookup<Game.Objects.Transform> m_TransformDatas;

            [ReadOnly]
            public ComponentLookup<PropertyRenter> m_PropertyRenters;

            [ReadOnly]
            public ComponentLookup<PrefabRef> m_Prefabs;

            [ReadOnly]
            public ComponentLookup<ServiceCompanyData> m_ServiceCompanies;

            [ReadOnly]
            public BufferLookup<OwnedVehicle> m_OwnedVehicles;

            [ReadOnly]
            public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;

            [ReadOnly]
            public BufferLookup<HouseholdAnimal> m_HouseholdAnimals;

            [ReadOnly]
            public ComponentLookup<ResourceData> m_ResourceDatas;

            [ReadOnly]
            public ComponentLookup<Game.Companies.StorageCompany> m_Storages;

            public ComponentLookup<CompanyStatisticData> m_CompanyStatistics;

            public BufferLookup<TradeCost> m_TradeCosts;

            [ReadOnly]
            public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;

            [ReadOnly]
            public ResourcePrefabs m_ResourcePrefabs;

            [ReadOnly]
            public PersonalCarSelectData m_PersonalCarSelectData;

            public NativeArray<int> m_CitizenConsumptionAccumulator;

            public Entity m_PopulationEntity;

            public RandomSeed m_RandomSeed;

            public EntityCommandBuffer m_CommandBuffer;

            [ReadOnly]
            public uint m_FrameIndex;

            public void Execute()
            {
                Unity.Mathematics.Random random = m_RandomSeed.GetRandom(0);

                SalesEvent item;
                while (m_SalesQueue.TryDequeue(out item))
                {
                    if (item.m_Amount == 0 || !m_Resources.TryGetBuffer(item.m_Buyer, out var buyerResources))
                    {
                        continue;
                    }

                    Entity resourcePrefab = m_ResourcePrefabs[item.m_Resource];
                    bool isVirtual = m_ResourceDatas[resourcePrefab].m_Weight == 0f || (item.m_Flags & SaleFlags.Virtual) != 0;
                    if (!isVirtual && (item.m_Flags & SaleFlags.ImportFromOC) == 0 && m_Resources.TryGetBuffer(item.m_Seller, out var resourceSeller2) && EconomyUtils.GetResources(item.m_Resource, resourceSeller2) <= 0)
                    {
                        continue;
                    }

                    bool flag = (item.m_Flags & SaleFlags.CommercialSeller) != 0;
                    float num = (flag ? EconomyUtils.GetMarketPrice(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas) : EconomyUtils.GetIndustrialPrice(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas)) * (float)item.m_Amount;

                    if (m_TradeCosts.TryGetBuffer(item.m_Seller, out DynamicBuffer<TradeCost> sellerCosts))
                    {
                        bool buyerHasTradeCosts = m_TradeCosts.TryGetBuffer(item.m_Buyer, out DynamicBuffer<TradeCost> buyerCosts);

                        TradeCost tradeCost = EconomyUtils.GetTradeCost(item.m_Resource, sellerCosts);
                        num += (float)item.m_Amount * tradeCost.m_BuyCost;
                        float weight = EconomyUtils.GetWeight(item.m_Resource, m_ResourcePrefabs, ref m_ResourceDatas);
                        float num2 = (float)EconomyUtils.GetTransportCost(item.m_Distance, item.m_Resource, item.m_Amount, weight) / (1f + (float)item.m_Amount);
                        TradeCost newcost = default(TradeCost);
                        if (buyerHasTradeCosts)
                        {
                            newcost = EconomyUtils.GetTradeCost(item.m_Resource, buyerCosts);
                        }
                        if (!flag && !m_OutsideConnections.HasComponent(item.m_Seller))
                        {
                            tradeCost.m_SellCost = math.lerp(tradeCost.m_SellCost, num2 + newcost.m_SellCost, 0.5f);
                            EconomyUtils.SetTradeCost(item.m_Resource, tradeCost, sellerCosts, keepLastTime: true);
                        }
                        if (buyerHasTradeCosts && !m_OutsideConnections.HasComponent(item.m_Buyer))
                        {
                            if (num2 + tradeCost.m_BuyCost < newcost.m_BuyCost)
                            {
                                newcost.m_BuyCost = num2 + tradeCost.m_BuyCost;
                            }
                            else
                            {
                                newcost.m_BuyCost = math.lerp(newcost.m_BuyCost, num2 + tradeCost.m_BuyCost, 0.5f);
                            }
                            EconomyUtils.SetTradeCost(item.m_Resource, newcost, buyerCosts, keepLastTime: true);
                        }
                    }

                    bool sellerHasPropertyRenter = m_PropertyRenters.TryGetComponent(item.m_Seller, out PropertyRenter sellerPropertyRenter);
                    bool sellerIsStorage = m_Storages.HasComponent(item.m_Seller);

                    if (flag && sellerHasPropertyRenter && m_Services.TryGetComponent(item.m_Seller, out ServiceAvailable serviceSeller))
                    {
                        Entity prefab = m_Prefabs[item.m_Seller].m_Prefab;
                        ServiceCompanyData serviceCompanyData = m_ServiceCompanies[prefab];
                        num *= EconomyUtils.GetServicePriceMultiplier(serviceSeller.m_ServiceAvailable, serviceCompanyData.m_MaxService);

                        serviceSeller.m_ServiceAvailable = math.max(0, Mathf.RoundToInt(serviceSeller.m_ServiceAvailable - item.m_Amount));
                        if (serviceSeller.m_MeanPriority > 0f)
                        {
                            serviceSeller.m_MeanPriority = math.min(1f, math.lerp(serviceSeller.m_MeanPriority, (float)serviceSeller.m_ServiceAvailable / (float)serviceCompanyData.m_MaxService, 0.1f));
                        }
                        else
                        {
                            serviceSeller.m_MeanPriority = math.min(1f, (float)serviceSeller.m_ServiceAvailable / (float)serviceCompanyData.m_MaxService);
                        }
                        m_Services[item.m_Seller] = serviceSeller;
                    }


                    if (!sellerIsStorage && m_Resources.TryGetBuffer(item.m_Seller, out var sellerWriteResources))
                    {
                        int resources2 = EconomyUtils.GetResources(item.m_Resource, sellerWriteResources);
                        EconomyUtils.AddResources(item.m_Resource, -math.min(resources2, item.m_Amount), sellerWriteResources);
                    }
                    EconomyUtils.AddResources(Resource.Money, -Mathf.RoundToInt(num), buyerResources);


                    if (m_Households.TryGetComponent(item.m_Buyer, out Household householdValue))
                    {
                        householdValue.m_Resources = (int)math.clamp((long)((float)householdValue.m_Resources + num), -2147483648L, 2147483647L);
                        householdValue.m_ShoppedValuePerDay += (uint)math.max(0f, num);
                        m_Households[item.m_Buyer] = householdValue;
                        int resourceIndex = EconomyUtils.GetResourceIndex(item.m_Resource);
                        if (resourceIndex >= 0 && resourceIndex < m_CitizenConsumptionAccumulator.Length)
                        {
                            m_CitizenConsumptionAccumulator[resourceIndex] += item.m_Amount;
                        }
                    }
                    else if (m_BuyingCompanies.TryGetComponent(item.m_Buyer, out BuyingCompany buyingCompanyValue))
                    {
                        buyingCompanyValue.m_LastTradePartner = item.m_Seller;
                        m_BuyingCompanies[item.m_Buyer] = buyingCompanyValue;
                        if (isVirtual)
                        {
                            EconomyUtils.AddResources(item.m_Resource, item.m_Amount, buyerResources);
                        }
                    }
                    if (!sellerIsStorage && sellerHasPropertyRenter)
                    {
                        DynamicBuffer<Game.Economy.Resources> resources3 = m_Resources[item.m_Seller];
                        EconomyUtils.AddResources(Resource.Money, Mathf.RoundToInt(num), resources3);
                    }
                    if (m_CompanyStatistics.TryGetComponent(item.m_Seller, out CompanyStatisticData companyStatisticBuyer))
                    {
                        companyStatisticBuyer.m_CurrentNumberOfCustomers++;
                        m_CompanyStatistics[item.m_Seller] = companyStatisticBuyer;
                    }
                    if (m_CompanyStatistics.TryGetComponent(item.m_Buyer, out CompanyStatisticData companyStatisticSeller))
                    {
                        companyStatisticSeller.m_CurrentCostOfBuyingResources += math.abs((int)num);
                        m_CompanyStatistics[item.m_Buyer] = companyStatisticSeller;
                    }
                    if (item.m_Resource != Resource.Vehicles || item.m_Amount != HouseholdBehaviorSystem.kCarAmount || !sellerHasPropertyRenter)
                    {
                        continue;
                    }
                    Entity property = sellerPropertyRenter.m_Property;
                    if (
                        !m_TransformDatas.TryGetComponent(property, out Game.Objects.Transform transform) ||
                        !m_HouseholdCitizens.TryGetBuffer(item.m_Buyer, out DynamicBuffer<HouseholdCitizen> buyerCitizens))
                    {
                        continue;
                    }
                    Entity entity = item.m_Buyer;
                    int length = buyerCitizens.Length;
                    int num3 = (m_HouseholdAnimals.TryGetBuffer(entity, out DynamicBuffer<HouseholdAnimal> buyerAnimals) ? buyerAnimals.Length : 0);
                    int passengerAmount;
                    int num4;
                    bool hasOwnedVehicles = m_OwnedVehicles.TryGetBuffer(entity, out DynamicBuffer<OwnedVehicle> ownedVehicles);
                    if (hasOwnedVehicles && ownedVehicles.Length >= 1)
                    {
                        passengerAmount = random.NextInt(1, 1 + length);
                        num4 = random.NextInt(1, 2 + num3);
                    }
                    else
                    {
                        passengerAmount = length;
                        num4 = 1 + num3;
                    }
                    if (random.NextInt(20) == 0)
                    {
                        num4 += 5;
                    }
                    Entity entity2 = m_PersonalCarSelectData.CreateVehicle(m_CommandBuffer, ref random, passengerAmount, num4, avoidTrailers: true, noSlowVehicles: false, bicycle: false, transform, property, Entity.Null, (PersonalCarFlags)0u, stopped: true);
                    if (entity2 != Entity.Null)
                    {
                        m_CommandBuffer.AddComponent(entity2, new Owner(entity));
                        if (!hasOwnedVehicles)
                        {
                            m_CommandBuffer.AddBuffer<OwnedVehicle>(entity);
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private struct HandleBuyersJob : IJobChunk
        {
            [ReadOnly]
            public ComponentTypeHandle<ResourceBuyer> m_BuyerType;

            [ReadOnly]
            public ComponentTypeHandle<ResourceBought> m_BoughtType;

            [ReadOnly]
            public EntityTypeHandle m_EntityType;

            public BufferTypeHandle<TripNeeded> m_TripType;

            [ReadOnly]
            public ComponentTypeHandle<Citizen> m_CitizenType;

            [ReadOnly]
            public ComponentTypeHandle<CreatureData> m_CreatureDataType;

            [ReadOnly]
            public ComponentTypeHandle<ResidentData> m_ResidentDataType;

            [ReadOnly]
            public ComponentTypeHandle<AttendingMeeting> m_AttendingMeetingType;

            [ReadOnly]
            public ComponentLookup<PathInformation> m_PathInformation;

            [ReadOnly]
            public ComponentLookup<PropertyRenter> m_Properties;

            [ReadOnly]
            public ComponentLookup<ServiceAvailable> m_ServiceAvailables;

            [ReadOnly]
            public ComponentLookup<CarKeeper> m_CarKeepers;

            [ReadOnly]
            public ComponentLookup<BicycleOwner> m_BicycleOwners;

            [ReadOnly]
            public ComponentLookup<ParkedCar> m_ParkedCarData;

            [ReadOnly]
            public ComponentLookup<Game.Vehicles.PersonalCar> m_PersonalCarData;

            [ReadOnly]
            public ComponentLookup<Target> m_Targets;

            [ReadOnly]
            public ComponentLookup<CurrentBuilding> m_CurrentBuildings;

            [ReadOnly]
            public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;

            [ReadOnly]
            public ComponentLookup<HouseholdMember> m_HouseholdMembers;

            [ReadOnly]
            public ComponentLookup<Household> m_Households;

            [ReadOnly]
            public ComponentLookup<TouristHousehold> m_TouristHouseholds;

            [ReadOnly]
            public ComponentLookup<CommuterHousehold> m_CommuterHouseholds;

            [ReadOnly]
            public ComponentLookup<Worker> m_Workers;

            [ReadOnly]
            public ComponentLookup<Game.Vehicles.DeliveryTruck> m_DeliveryTrucks;

            [ReadOnly]
            public ComponentLookup<Game.Companies.StorageCompany> m_StorageCompanies;

            [ReadOnly]
            public BufferLookup<Game.Economy.Resources> m_Resources;

            [ReadOnly]
            public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;

            [ReadOnly]
            public BufferLookup<GuestVehicle> m_GuestVehicles;

            [ReadOnly]
            public BufferLookup<LayoutElement> m_LayoutElements;

            [NativeDisableParallelForRestriction]
            public ComponentLookup<CoordinatedMeeting> m_CoordinatedMeetings;

            [ReadOnly]
            public BufferLookup<HaveCoordinatedMeetingData> m_HaveCoordinatedMeetingDatas;

            [ReadOnly]
            public ResourcePrefabs m_ResourcePrefabs;

            [ReadOnly]
            public ComponentLookup<ResourceData> m_ResourceDatas;

            [ReadOnly]
            public ComponentLookup<PrefabRef> m_PrefabRefData;

            [ReadOnly]
            public ComponentLookup<CarData> m_PrefabCarData;

            [ReadOnly]
            public ComponentLookup<ObjectGeometryData> m_ObjectGeometryData;

            [ReadOnly]
            public ComponentLookup<OutsideConnectionData> m_OutsideConnectionDatas;

            [ReadOnly]
            public ComponentLookup<HumanData> m_PrefabHumanData;

            [ReadOnly]
            public ComponentLookup<Population> m_Populations;

            [ReadOnly]
            public ComponentLookup<CurrentDistrict> m_CurrentDistrictData;

            [ReadOnly]
            public BufferLookup<DistrictModifier> m_DistrictModifiers;

            [ReadOnly]
            public float m_TimeOfDay;

            [ReadOnly]
            public uint m_FrameIndex;

            [ReadOnly]
            public RandomSeed m_RandomSeed;

            [ReadOnly]
            public ComponentTypeSet m_PathfindTypes;

            [ReadOnly]
            public NativeList<ArchetypeChunk> m_HumanChunks;

            [ReadOnly]
            public PersonalCarSelectData m_PersonalCarSelectData;

            public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

            public NativeQueue<SetupQueueItem>.ParallelWriter m_PathfindQueue;

            public EconomyParameterData m_EconomyParameterData;

            [ReadOnly]
            public TripPriorityParametersData m_TripPriorityParameters;

            public Entity m_City;

            public NativeQueue<SalesEvent>.ParallelWriter m_SalesQueue;

            [ReadOnly]
            public NativeArray<int> m_BuyerOutcomes;

            [ReadOnly]
            public NativeArray<int> m_FailedShoppingOrigins;

            [ReadOnly]
            public NativeArray<int> m_FailedShoppingResources;

            private unsafe static void IncrementSlot(NativeArray<int> array, int index)
            {
                Interlocked.Increment(ref UnsafeUtility.ArrayElementAsRef<int>(NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array), index));
            }

            private void IncrementOutcome(BuyerOutcome outcome)
            {
                IncrementSlot(m_BuyerOutcomes, (int)outcome);
            }

            private FailedShoppingOrigin ClassifyShoppingOrigin(Entity buyerEntity)
            {
                Entity entity = Entity.Null;
                if (m_HouseholdMembers.TryGetComponent(buyerEntity, out var componentData) && m_Properties.TryGetComponent(componentData.m_Household, out var componentData2))
                {
                    entity = componentData2.m_Property;
                }
                if (entity == Entity.Null)
                {
                    return FailedShoppingOrigin.NoHome;
                }
                if (!m_CurrentBuildings.TryGetComponent(buyerEntity, out var componentData3))
                {
                    return FailedShoppingOrigin.NoCurrentBuilding;
                }
                Entity currentBuilding = componentData3.m_CurrentBuilding;
                if (currentBuilding == entity)
                {
                    return FailedShoppingOrigin.AtHome;
                }
                if (m_Workers.TryGetComponent(buyerEntity, out var componentData4))
                {
                    Entity entity2 = (m_Properties.TryGetComponent(componentData4.m_Workplace, out var componentData5) ? componentData5.m_Property : componentData4.m_Workplace);
                    if (currentBuilding == entity2)
                    {
                        return FailedShoppingOrigin.AtWorkplace;
                    }
                }
                if (m_OutsideConnections.HasComponent(currentBuilding))
                {
                    return FailedShoppingOrigin.AtOutsideConnection;
                }
                return FailedShoppingOrigin.AtOther;
            }

            private void RecordCitizenPathfindFailure(Entity buyerEntity, Resource resource)
            {
                IncrementSlot(m_FailedShoppingOrigins, (int)ClassifyShoppingOrigin(buyerEntity));
                int index = ((resource != Resource.NoResource) ? (EconomyUtils.GetResourceIndex(resource) + 1) : 0);
                IncrementSlot(m_FailedShoppingResources, index);
            }

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
                NativeArray<ResourceBuyer> nativeArray2 = chunk.GetNativeArray(ref m_BuyerType);
                NativeArray<ResourceBought> nativeArray3 = chunk.GetNativeArray(ref m_BoughtType);
                BufferAccessor<TripNeeded> bufferAccessor = chunk.GetBufferAccessor(ref m_TripType);
                NativeArray<Citizen> nativeArray4 = chunk.GetNativeArray(ref m_CitizenType);
                NativeArray<AttendingMeeting> nativeArray5 = chunk.GetNativeArray(ref m_AttendingMeetingType);
                Unity.Mathematics.Random random = m_RandomSeed.GetRandom(unfilteredChunkIndex);
                ProcessResourceBought(unfilteredChunkIndex, nativeArray3, nativeArray);
                ProcessResourceBuyer(chunk, unfilteredChunkIndex, nativeArray2, nativeArray, bufferAccessor, nativeArray4, random, nativeArray5);
            }

            private void ProcessResourceBought(int unfilteredChunkIndex, NativeArray<ResourceBought> resourceBuyingWithTargets, NativeArray<Entity> entities)
            {
                for (int i = 0; i < resourceBuyingWithTargets.Length; i++)
                {
                    Entity e = entities[i];
                    ResourceBought resourceBought = resourceBuyingWithTargets[i];
                    if (m_PrefabRefData.HasComponent(resourceBought.m_Payer) && m_PrefabRefData.HasComponent(resourceBought.m_Seller))
                    {
                        SalesEvent value = new SalesEvent
                        {
                            m_Amount = resourceBought.m_Amount,
                            m_Buyer = resourceBought.m_Payer,
                            m_Seller = resourceBought.m_Seller,
                            m_Resource = resourceBought.m_Resource,
                            m_Flags = SaleFlags.None,
                            m_Distance = resourceBought.m_Distance
                        };
                        m_SalesQueue.Enqueue(value);
                    }
                    m_CommandBuffer.RemoveComponent<ResourceBought>(unfilteredChunkIndex, e);
                }
            }

            private void ProcessResourceBuyer(ArchetypeChunk chunk, int unfilteredChunkIndex, NativeArray<ResourceBuyer> resourceBuyingRequests, NativeArray<Entity> entities, BufferAccessor<TripNeeded> tripBuffers, NativeArray<Citizen> citizens, Unity.Mathematics.Random random, NativeArray<AttendingMeeting> meetings)
            {
                if (resourceBuyingRequests.Length == 0)
                {
                    return;
                }

                bool flag = citizens.Length > 0;

                int population = m_Populations[m_City].m_Population;
                float trafficFactor = math.max(1f, math.sqrt(m_EconomyParameterData.m_TrafficReduction * (float)population * 0.1f));
                int trafficThreshold = 100 - Mathf.RoundToInt(100f / trafficFactor);

                for (int i = 0; i < resourceBuyingRequests.Length; i++)
                {
                    ResourceBuyer resourceBuyer = resourceBuyingRequests[i];
                    Entity entity = entities[i];

                    DynamicBuffer<TripNeeded> dynamicBuffer = tripBuffers[i];
                    bool flag2 = false;
                    Entity entity2 = m_ResourcePrefabs[resourceBuyer.m_ResourceNeeded];
                    if (m_ResourceDatas.HasComponent(entity2))
                    {
                        flag2 = EconomyUtils.GetWeight(resourceBuyer.m_ResourceNeeded, m_ResourcePrefabs, ref m_ResourceDatas) == 0f;
                    }
                    if (m_PathInformation.TryGetComponent(entity, out PathInformation pathInformation))
                    {
                        if ((pathInformation.m_State & PathFlags.Pending) != 0)
                        {
                            continue;
                        }
                        Entity destination = pathInformation.m_Destination;
                        bool flag3 = m_OutsideConnections.HasComponent(destination);
                        if (m_Properties.HasComponent(destination) | flag3)
                        {
                            int num;
                            if (flag2 & flag3)
                            {
                                num = resourceBuyer.m_AmountNeeded;
                            }
                            else
                            {
                                DynamicBuffer<Game.Economy.Resources> resources = m_Resources[destination];
                                num = EconomyUtils.GetResources(resourceBuyer.m_ResourceNeeded, resources);
                                if (m_StorageCompanies.HasComponent(destination))
                                {
                                    int allBuyingResourcesTrucks = VehicleUtils.GetAllBuyingResourcesTrucks(destination, resourceBuyer.m_ResourceNeeded, ref m_DeliveryTrucks, ref m_GuestVehicles, ref m_LayoutElements);
                                    num -= allBuyingResourcesTrucks;
                                }
                                if (num <= 0 || (!flag3 && num < resourceBuyer.m_AmountNeeded / 2))
                                {
                                    IncrementOutcome(flag ? BuyerOutcome.CitizenInsufficientStock : BuyerOutcome.CompanyInsufficientStock);
                                    m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
                                    continue;
                                }
                            }
                            resourceBuyer.m_AmountNeeded = math.min(resourceBuyer.m_AmountNeeded, num);
                            bool num2 = m_ServiceAvailables.HasComponent(destination);
                            bool flag4 = m_StorageCompanies.HasComponent(destination);
                            SaleFlags saleFlags = (num2 ? SaleFlags.CommercialSeller : SaleFlags.None);
                            if (flag2)
                            {
                                saleFlags |= SaleFlags.Virtual;
                            }
                            if (flag3)
                            {
                                saleFlags |= SaleFlags.ImportFromOC;
                            }
                            if (m_Households.TryGetComponent(resourceBuyer.m_Payer, out Household householdData) && m_Resources.TryGetBuffer(resourceBuyer.m_Payer, out DynamicBuffer<Game.Economy.Resources> payerResources))
                            {
                                int dailyShoppingAllowance = math.max(1500, householdData.m_Income / 2);

                                // FIX A: If this is a virtual good AND the household has already shopped today, skip!
                                if (flag2 && householdData.m_ShoppedValuePerDay > dailyShoppingAllowance)
                                {
                                    resourceBuyer.m_AmountNeeded = 0; // Throttled: Already bought digital goods today!
                                }
                                else
                                {

                                    int currentMoney = EconomyUtils.GetResources(Resource.Money, payerResources);

                                    // 1. Look up actual monthly rent for this household
                                    int rentReserve = 0;
                                    if (m_Properties.TryGetComponent(resourceBuyer.m_Payer, out var propertyRenter))
                                    {
                                        rentReserve = propertyRenter.m_Rent;
                                    }

                                    // 2. Lock 2 months of rent + baseline reserve before allowing luxury shopping
                                    int safetyReserve = math.max(HouseholdBehaviorSystem.kMinimumShoppingMoney, rentReserve * 2);
                                    int availableSurplus = math.max(0, currentMoney - safetyReserve);

                                    float marketPrice = EconomyUtils.GetMarketPrice(resourceBuyer.m_ResourceNeeded, m_ResourcePrefabs, ref m_ResourceDatas);
                                    float markup = 1.4f;

                                    // 3. Fraction of surplus allowed: only spend up to 25% of disposable surplus per trip
                                    int maxDiscretionarySpend = (int)(availableSurplus * 0.25f);
                                    int y = (((float)maxDiscretionarySpend > 0f) ? ((int)((float)maxDiscretionarySpend / (marketPrice * markup))) : 0);

                                    resourceBuyer.m_AmountNeeded = math.min(resourceBuyer.m_AmountNeeded, y);

                                    if (flag2) // flag2 is already defined on line 605 as (Weight == 0f)
                                    {
                                        int householdSize = m_HouseholdCitizens.TryGetBuffer(resourceBuyer.m_Payer, out var payerCitizens)
                                            ? payerCitizens.Length
                                            : 1;

                                        // Caps order to max 3 units per family member (e.g. 3 to 9 units per order)
                                        int maxVirtualUnits = math.max(2, householdSize * 3);
                                        resourceBuyer.m_AmountNeeded = math.min(resourceBuyer.m_AmountNeeded, maxVirtualUnits);
                                    }
                                }
                            }
                            bool flag5 = resourceBuyer.m_AmountNeeded > 0;
                            if (flag5)
                            {
                                IncrementOutcome(flag ? BuyerOutcome.CitizenSuccess : BuyerOutcome.CompanySuccess);
                                SalesEvent value = new SalesEvent
                                {
                                    m_Amount = resourceBuyer.m_AmountNeeded,
                                    m_Buyer = resourceBuyer.m_Payer,
                                    m_Seller = destination,
                                    m_Resource = resourceBuyer.m_ResourceNeeded,
                                    m_Flags = saleFlags,
                                    m_Distance = pathInformation.m_Distance
                                };
                                m_SalesQueue.Enqueue(value);
                            }
                            else if (flag)
                            {
                                IncrementOutcome(BuyerOutcome.CitizenCantAfford);
                            }
                            m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
                            m_CommandBuffer.RemoveComponent<ResourceBuyer>(unfilteredChunkIndex, entity);
                            bool flag6 = citizens.Length > 0 && random.NextInt(100) < trafficThreshold;
                            if ((!flag2 && !flag6) & flag5)
                            {
                                m_CommandBuffer.AddBuffer<CurrentTrading>(unfilteredChunkIndex, entity).Add(new CurrentTrading
                                {
                                    m_TradingResource = resourceBuyer.m_ResourceNeeded,
                                    m_TradingResourceAmount = resourceBuyer.m_AmountNeeded,
                                    m_OutsideConnectionType = (m_OutsideConnections.HasComponent(destination) ? BuildingUtils.GetOutsideConnectionType(destination, ref m_PrefabRefData, ref m_OutsideConnectionDatas) : OutsideConnectionTransferType.None),
                                    m_TradingStartFrameIndex = m_FrameIndex
                                });
                                dynamicBuffer.Add(new TripNeeded
                                {
                                    m_TargetAgent = destination,
                                    m_Purpose = ((!flag4) ? Purpose.Shopping : Purpose.CompanyShopping),
                                    m_Data = resourceBuyer.m_AmountNeeded,
                                    m_Resource = resourceBuyer.m_ResourceNeeded,
                                    m_Priority = 128
                                });
                                if (!m_Targets.HasComponent(entities[i]))
                                {
                                    m_CommandBuffer.AddComponent(unfilteredChunkIndex, entity, new Target
                                    {
                                        m_Target = destination
                                    });
                                }
                            }
                            continue;
                        }
                        bool flag7 = pathInformation.m_TotalCost < 0f;
                        if (flag)
                        {
                            if (flag7)
                            {
                                IncrementOutcome(BuyerOutcome.CitizenPathfindFailed);
                                RecordCitizenPathfindFailure(entity, resourceBuyer.m_ResourceNeeded);
                            }
                            else
                            {
                                IncrementOutcome(BuyerOutcome.CitizenStaleDestination);
                            }
                        }
                        else
                        {
                            IncrementOutcome(flag7 ? BuyerOutcome.CompanyPathfindFailed : BuyerOutcome.CompanyStaleDestination);
                        }
                        m_CommandBuffer.RemoveComponent<ResourceBuyer>(unfilteredChunkIndex, entity);
                        m_CommandBuffer.RemoveComponent(unfilteredChunkIndex, entity, in m_PathfindTypes);
                        if (meetings.IsCreated)
                        {
                            AttendingMeeting attendingMeeting = meetings[i];
                            Entity prefab = m_PrefabRefData[attendingMeeting.m_Meeting].m_Prefab;
                            CoordinatedMeeting value2 = m_CoordinatedMeetings[attendingMeeting.m_Meeting];
                            if (m_HaveCoordinatedMeetingDatas[prefab][value2.m_Phase].m_TravelPurpose.m_Purpose == Purpose.Shopping)
                            {
                                value2.m_Status = MeetingStatus.Done;
                                m_CoordinatedMeetings[attendingMeeting.m_Meeting] = value2;
                            }
                        }
                    }
                    else if (
                        !meetings.IsCreated &&
                            (
                                !m_HouseholdMembers.TryGetComponent(entity, out HouseholdMember householdMember) ||
                                (
                                    !m_TouristHouseholds.HasComponent(householdMember.m_Household) &&
                                    !m_CommuterHouseholds.HasComponent(householdMember.m_Household)
                                 )
                            ) &&
                        m_CurrentBuildings.TryGetComponent(entity, out CurrentBuilding currentBuilding) &&
                        m_OutsideConnections.HasComponent(currentBuilding.m_CurrentBuilding))
                    {
                        SaleFlags flags = SaleFlags.ImportFromOC;
                        SalesEvent value3 = new SalesEvent
                        {
                            m_Amount = resourceBuyer.m_AmountNeeded,
                            m_Buyer = resourceBuyer.m_Payer,
                            m_Seller = currentBuilding.m_CurrentBuilding,
                            m_Resource = resourceBuyer.m_ResourceNeeded,
                            m_Flags = flags,
                            m_Distance = 0f
                        };
                        IncrementOutcome(BuyerOutcome.CitizenAtOutsideConnection);
                        m_SalesQueue.Enqueue(value3);
                        m_CommandBuffer.RemoveComponent<ResourceBuyer>(unfilteredChunkIndex, entity);
                    }
                    else
                    {
                        Citizen citizen = default(Citizen);
                        if (citizens.Length > 0)
                        {
                            citizen = citizens[i];
                            Entity household = m_HouseholdMembers[entity].m_Household;
                            Household householdData = m_Households[household];
                            DynamicBuffer<HouseholdCitizen> dynamicBuffer2 = m_HouseholdCitizens[household];
                            FindShopForCitizen(ref random, chunk, unfilteredChunkIndex, entity, resourceBuyer.m_ResourceNeeded, resourceBuyer.m_AmountNeeded, resourceBuyer.m_Flags, citizen, householdData, dynamicBuffer2.Length, flag2);
                        }
                        else
                        {
                            FindShopForCompany(chunk, unfilteredChunkIndex, entity, resourceBuyer.m_ResourceNeeded, resourceBuyer.m_AmountNeeded, resourceBuyer.m_Flags, flag2);
                        }
                    }
                }
            }

            private void FindShopForCitizen(ref Unity.Mathematics.Random random, ArchetypeChunk chunk, int index, Entity buyer, Resource resource, int amount, SetupTargetFlags flags, Citizen citizenData, Household householdData, int householdCitizenCount, bool virtualGood)
            {
                IncrementOutcome(BuyerOutcome.CitizenSearchStarted);
                if (virtualGood)
                {
                    flags |= SetupTargetFlags.Import;
                }
                m_CommandBuffer.AddComponent(index, buyer, in m_PathfindTypes);
                m_CommandBuffer.SetComponent(index, buyer, new PathInformation
                {
                    m_State = PathFlags.Pending
                });
                Entity entity = ObjectEmergeSystem.SelectResidentPrefab(citizenData, m_HumanChunks, m_EntityType, ref m_CreatureDataType, ref m_ResidentDataType, out var creatureData, out var _);
                HumanData humanData = default(HumanData);
                if (entity != Entity.Null)
                {
                    humanData = m_PrefabHumanData[entity];
                }
                PathfindParameters parameters = new PathfindParameters
                {
                    m_MaxSpeed = 277.77777f,
                    m_WalkSpeed = humanData.m_WalkSpeed,
                    m_Weights = CitizenUtils.GetPathfindWeights(citizenData, householdData, householdCitizenCount),
                    m_Methods = (PathMethod.Pedestrian | PathMethod.Taxi | RouteUtils.GetPublicTransportMethods(m_TimeOfDay)),
                    m_TaxiIgnoredRules = VehicleUtils.GetIgnoredPathfindRulesTaxiDefaults(),
                    m_MaxCost = m_TripPriorityParameters.GetMaxCost(m_TripPriorityParameters.m_PriorityShopping)
                };
                SetupQueueTarget origin = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = PathMethod.Pedestrian,
                    m_RandomCost = 30f
                };
                SetupQueueTarget destination = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.ResourceSeller,
                    m_Methods = PathMethod.Pedestrian,
                    m_Resource = resource,
                    m_Value = amount,
                    m_Flags = flags,
                    m_RandomCost = 30f,
                    m_ActivityMask = creatureData.m_SupportedActivities
                };
                if (virtualGood)
                {
                    parameters.m_PathfindFlags |= PathfindFlags.SkipPathfind;
                }
                Entity entity2 = Entity.Null;
                if (m_HouseholdMembers.TryGetComponent(buyer, out var componentData) && m_Properties.TryGetComponent(componentData.m_Household, out var componentData2))
                {
                    entity2 = componentData2.m_Property;
                    parameters.m_Authorization1 = componentData2.m_Property;
                }
                if (m_Workers.TryGetComponent(buyer, out Worker worker))
                {
                    if (m_Properties.TryGetComponent(worker.m_Workplace, out PropertyRenter workplaceProperty))
                    {
                        parameters.m_Authorization2 = workplaceProperty.m_Property;
                    }
                    else
                    {
                        parameters.m_Authorization2 = worker.m_Workplace;
                    }
                }
                if (m_CarKeepers.IsComponentEnabled(buyer))
                {
                    Entity car = m_CarKeepers[buyer].m_Car;
                    if (m_ParkedCarData.TryGetComponent(car, out ParkedCar parkedCar))
                    {
                        PrefabRef prefabRef = m_PrefabRefData[car];
                        CarData carData = m_PrefabCarData[prefabRef.m_Prefab];
                        parameters.m_MaxSpeed.x = carData.m_MaxSpeed;
                        parameters.m_ParkingTarget = parkedCar.m_Lane;
                        parameters.m_ParkingDelta = parkedCar.m_CurvePosition;
                        parameters.m_ParkingSize = VehicleUtils.GetParkingSize(car, ref m_PrefabRefData, ref m_ObjectGeometryData);
                        parameters.m_Methods |= VehicleUtils.GetPathMethods(carData) | PathMethod.Parking;
                        parameters.m_IgnoredRules = VehicleUtils.GetIgnoredPathfindRules(carData);
                        if (m_PersonalCarData.TryGetComponent(car, out var componentData4) && (componentData4.m_State & PersonalCarFlags.HomeTarget) == 0)
                        {
                            parameters.m_PathfindFlags |= PathfindFlags.ParkingReset;
                        }
                    }
                }
                else if (m_BicycleOwners.IsComponentEnabled(buyer))
                {
                    float value = 20f;
                    if (m_CurrentDistrictData.TryGetComponent(entity2, out var componentData3) && m_DistrictModifiers.TryGetBuffer(componentData3.m_District, out var bufferData))
                    {
                        AreaUtils.ApplyModifier(ref value, bufferData, DistrictModifierType.BikeProbability);
                    }

                    if (random.NextFloat(100f) < value)
                    {
                        Entity bicycle = m_BicycleOwners[buyer].m_Bicycle;
                        if (!m_PrefabRefData.TryGetComponent(bicycle, out var componentData5) && m_CurrentBuildings.TryGetComponent(buyer, out var componentData6) && componentData6.m_CurrentBuilding == entity2)
                        {
                            Unity.Mathematics.Random random2 = citizenData.GetPseudoRandom(CitizenPseudoRandom.BicycleModel);
                            componentData5.m_Prefab = m_PersonalCarSelectData.SelectVehiclePrefab(ref random2, 1, 0, avoidTrailers: true, noSlowVehicles: false, bicycle: true, out var _);
                        }
                        if (m_PrefabCarData.TryGetComponent(componentData5.m_Prefab, out var componentData7) && m_ObjectGeometryData.TryGetComponent(componentData5.m_Prefab, out var componentData8))
                        {
                            parameters.m_MaxSpeed.x = componentData7.m_MaxSpeed;
                            parameters.m_ParkingSize = VehicleUtils.GetParkingSize(componentData8, out var _);
                            parameters.m_Methods |= PathMethod.Bicycle | PathMethod.BicycleParking;
                            parameters.m_IgnoredRules = VehicleUtils.GetIgnoredPathfindRulesBicycleDefaults();
                            if (m_ParkedCarData.TryGetComponent(bicycle, out var componentData9))
                            {
                                parameters.m_ParkingTarget = componentData9.m_Lane;
                                parameters.m_ParkingDelta = componentData9.m_CurvePosition;
                                if (m_PersonalCarData.TryGetComponent(bicycle, out var componentData10) && (componentData10.m_State & PersonalCarFlags.HomeTarget) == 0)
                                {
                                    parameters.m_PathfindFlags |= PathfindFlags.ParkingReset;
                                }
                            }
                            else
                            {
                                origin.m_Methods |= PathMethod.Bicycle;
                                origin.m_RoadTypes |= RoadTypes.Bicycle;
                            }
                        }
                    }
                }
                SetupQueueItem value2 = new SetupQueueItem(buyer, parameters, origin, destination);
                m_PathfindQueue.Enqueue(value2);
            }

            private void FindShopForCompany(ArchetypeChunk chunk, int index, Entity buyer, Resource resource, int amount, SetupTargetFlags flags, bool virtualGood)
            {
                IncrementOutcome(BuyerOutcome.CompanySearchStarted);
                m_CommandBuffer.AddComponent(index, buyer, in m_PathfindTypes);
                m_CommandBuffer.SetComponent(index, buyer, new PathInformation
                {
                    m_State = PathFlags.Pending
                });
                float transportCost = EconomyUtils.GetTransportCost(1f, amount, m_ResourceDatas[m_ResourcePrefabs[resource]].m_Weight, StorageTransferFlags.Car);
                PathfindParameters parameters = new PathfindParameters
                {
                    m_MaxSpeed = 111.111115f,
                    m_WalkSpeed = 5.555556f,
                    m_Weights = new PathfindWeights(0.01f, 0.01f, transportCost, 0.01f),
                    m_Methods = (PathMethod.Road | PathMethod.CargoLoading),
                    m_IgnoredRules = (RuleFlags.ForbidSlowTraffic | RuleFlags.AvoidBicycles)
                };
                SetupQueueTarget origin = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = (PathMethod.Road | PathMethod.CargoLoading),
                    m_RoadTypes = RoadTypes.Car
                };
                SetupQueueTarget destination = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.ResourceSeller,
                    m_Methods = (PathMethod.Road | PathMethod.CargoLoading),
                    m_RoadTypes = RoadTypes.Car,
                    m_Resource = resource,
                    m_Value = amount,
                    m_Flags = flags
                };
                if (virtualGood)
                {
                    parameters.m_PathfindFlags |= PathfindFlags.SkipPathfind;
                }
                SetupQueueItem value = new SetupQueueItem(buyer, parameters, origin, destination);
                m_PathfindQueue.Enqueue(value);
            }

            void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
            }
        }

        private const int UPDATE_INTERVAL = 16;

        private EntityQuery m_BuyerQuery;

        private EntityQuery m_CarPrefabQuery;

        private EntityQuery m_EconomyParameterQuery;

        private EntityQuery m_ResidentPrefabQuery;

        private EntityQuery m_PopulationQuery;

        private EntityQuery m_TripPriorityParametersQuery;

        private ComponentTypeSet m_PathfindTypes;

        private EndFrameBarrier m_EndFrameBarrier;

        private PathfindSetupSystem m_PathfindSetupSystem;

        private ResourceSystem m_ResourceSystem;

        private SimulationSystem m_SimulationSystem;

        private TimeSystem m_TimeSystem;

        private CityConfigurationSystem m_CityConfigurationSystem;

        private PersonalCarSelectData m_PersonalCarSelectData;

        private CitySystem m_CitySystem;

        private CityProductionStatisticSystem m_CityProductionStatisticSystem;

        [EnumArray(typeof(BuyerOutcome))]
        [DebugWatchValue]
        private NativeArray<int> m_BuyerOutcomes;

        [EnumArray(typeof(FailedShoppingOrigin))]
        [DebugWatchValue]
        private NativeArray<int> m_FailedShoppingOrigins;

        [EnumArray(typeof(ResourceInEditor))]
        [DebugWatchValue]
        private NativeArray<int> m_FailedShoppingResources;

        [DebugWatchDeps]
        private JobHandle m_WriteDeps;

        private NativeQueue<SalesEvent> m_SalesQueue;

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return UPDATE_INTERVAL;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = base.World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_PathfindSetupSystem = base.World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            m_ResourceSystem = base.World.GetOrCreateSystemManaged<ResourceSystem>();
            m_TimeSystem = base.World.GetOrCreateSystemManaged<TimeSystem>();
            m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_PersonalCarSelectData = new PersonalCarSelectData(this);
            m_CitySystem = base.World.GetOrCreateSystemManaged<CitySystem>();
            m_CityProductionStatisticSystem = base.World.GetOrCreateSystemManaged<CityProductionStatisticSystem>();
            m_SimulationSystem = base.World.GetOrCreateSystemManaged<SimulationSystem>();

            m_SalesQueue = new NativeQueue<SalesEvent>(Allocator.Persistent);
            m_BuyerOutcomes = new NativeArray<int>(12, Allocator.Persistent);
            m_FailedShoppingOrigins = new NativeArray<int>(6, Allocator.Persistent);
            m_FailedShoppingResources = new NativeArray<int>(42, Allocator.Persistent);

            m_BuyerQuery = SystemAPI.QueryBuilder()
                .WithAllRW<ResourceBuyer, TripNeeded>()
                .WithNone<TravelPurpose, Deleted, Temp>()
                .AddAdditionalQuery()
                .WithAll<ResourceBought>()
                .WithNone<Deleted, Temp>()
                .Build();

            m_EconomyParameterQuery = SystemAPI.QueryBuilder()
                .WithAll<EconomyParameterData>()
                .Build();

            m_PopulationQuery = SystemAPI.QueryBuilder()
                .WithAll<Population>()
                .Build();

            m_ResidentPrefabQuery = SystemAPI.QueryBuilder()
                .WithAll<ObjectData, HumanData, ResidentData, PrefabData>()
                .Build();

            m_TripPriorityParametersQuery = SystemAPI.QueryBuilder()
                .WithAll<TripPriorityParametersData>()
                .Build();

            m_CarPrefabQuery = GetEntityQuery(PersonalCarSelectData.GetEntityQueryDesc());
            m_PathfindTypes = new ComponentTypeSet(ComponentType.ReadWrite<PathInformation>(), ComponentType.ReadWrite<PathElement>());

            RequireForUpdate(m_BuyerQuery);
            RequireForUpdate(m_EconomyParameterQuery);
            RequireForUpdate(m_PopulationQuery);
            RequireForUpdate(m_TripPriorityParametersQuery);
        }

        protected override void OnDestroy()
        {
            m_SalesQueue.Dispose();
            m_BuyerOutcomes.Dispose();
            m_FailedShoppingOrigins.Dispose();
            m_FailedShoppingResources.Dispose();
            base.OnDestroy();
        }

        protected override void OnStopRunning()
        {
            base.OnStopRunning();
        }

        protected override void OnUpdate()
        {
            if (m_BuyerQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            m_PersonalCarSelectData.PreUpdate(this, m_CityConfigurationSystem, m_CarPrefabQuery, Allocator.TempJob, out var jobHandle);
            HandleBuyersJob buyerJob = new HandleBuyersJob
            {
                m_EntityType = SystemAPI.GetEntityTypeHandle(),
                m_BuyerType = SystemAPI.GetComponentTypeHandle<ResourceBuyer>(true),
                m_BoughtType = SystemAPI.GetComponentTypeHandle<ResourceBought>(true),
                m_TripType = SystemAPI.GetBufferTypeHandle<TripNeeded>(),
                m_CitizenType = SystemAPI.GetComponentTypeHandle<Citizen>(true),
                m_CreatureDataType = SystemAPI.GetComponentTypeHandle<CreatureData>(true),
                m_ResidentDataType = SystemAPI.GetComponentTypeHandle<ResidentData>(true),
                m_AttendingMeetingType = SystemAPI.GetComponentTypeHandle<AttendingMeeting>(true),

                m_ServiceAvailables = SystemAPI.GetComponentLookup<ServiceAvailable>(true),
                m_PathInformation = SystemAPI.GetComponentLookup<PathInformation>(true),
                m_Properties = SystemAPI.GetComponentLookup<PropertyRenter>(true),
                m_CarKeepers = SystemAPI.GetComponentLookup<CarKeeper>(true),
                m_BicycleOwners = SystemAPI.GetComponentLookup<BicycleOwner>(true),
                m_ParkedCarData = SystemAPI.GetComponentLookup<ParkedCar>(true),
                m_PersonalCarData = SystemAPI.GetComponentLookup<Game.Vehicles.PersonalCar>(true),
                m_Targets = SystemAPI.GetComponentLookup<Target>(true),
                m_CurrentBuildings = SystemAPI.GetComponentLookup<CurrentBuilding>(true),
                m_OutsideConnections = SystemAPI.GetComponentLookup<Game.Objects.OutsideConnection>(true),
                m_HouseholdMembers = SystemAPI.GetComponentLookup<HouseholdMember>(true),
                m_Households = SystemAPI.GetComponentLookup<Household>(true),
                m_TouristHouseholds = SystemAPI.GetComponentLookup<TouristHousehold>(true),
                m_CommuterHouseholds = SystemAPI.GetComponentLookup<CommuterHousehold>(true),
                m_Workers = SystemAPI.GetComponentLookup<Worker>(true),
                m_DeliveryTrucks = SystemAPI.GetComponentLookup<Game.Vehicles.DeliveryTruck>(true),
                m_StorageCompanies = SystemAPI.GetComponentLookup<Game.Companies.StorageCompany>(true),

                m_Resources = SystemAPI.GetBufferLookup<Game.Economy.Resources>(true),
                m_HouseholdCitizens = SystemAPI.GetBufferLookup<HouseholdCitizen>(true),
                m_GuestVehicles = SystemAPI.GetBufferLookup<GuestVehicle>(true),
                m_LayoutElements = SystemAPI.GetBufferLookup<LayoutElement>(true),

                m_ResourcePrefabs = m_ResourceSystem.GetPrefabs(),
                m_ResourceDatas = SystemAPI.GetComponentLookup<ResourceData>(true),
                m_PrefabRefData = SystemAPI.GetComponentLookup<PrefabRef>(true),
                m_PrefabCarData = SystemAPI.GetComponentLookup<CarData>(true),
                m_ObjectGeometryData = SystemAPI.GetComponentLookup<ObjectGeometryData>(true),
                m_PrefabHumanData = SystemAPI.GetComponentLookup<HumanData>(true),
                m_CoordinatedMeetings = SystemAPI.GetComponentLookup<CoordinatedMeeting>(false),
                m_HaveCoordinatedMeetingDatas = SystemAPI.GetBufferLookup<HaveCoordinatedMeetingData>(true),
                m_OutsideConnectionDatas = SystemAPI.GetComponentLookup<OutsideConnectionData>(true),
                m_Populations = SystemAPI.GetComponentLookup<Population>(true),
                m_CurrentDistrictData = SystemAPI.GetComponentLookup<CurrentDistrict>(true),
                m_DistrictModifiers = SystemAPI.GetBufferLookup<DistrictModifier>(true),

                m_TimeOfDay = m_TimeSystem.normalizedTime,
                m_FrameIndex = m_SimulationSystem.frameIndex,
                m_RandomSeed = RandomSeed.Next(),
                m_PathfindTypes = m_PathfindTypes,
                m_HumanChunks = m_ResidentPrefabQuery.ToArchetypeChunkListAsync(World.UpdateAllocator.ToAllocator, out JobHandle chunkListHandle),
                m_PersonalCarSelectData = m_PersonalCarSelectData,
                m_PathfindQueue = m_PathfindSetupSystem.GetQueue(this, 80, 16).AsParallelWriter(),
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer().AsParallelWriter(),
                m_EconomyParameterData = m_EconomyParameterQuery.GetSingleton<EconomyParameterData>(),
                m_TripPriorityParameters = m_TripPriorityParametersQuery.GetSingleton<TripPriorityParametersData>(),
                m_City = m_CitySystem.City,
                m_SalesQueue = m_SalesQueue.AsParallelWriter(),

                m_BuyerOutcomes = m_BuyerOutcomes,
                m_FailedShoppingOrigins = m_FailedShoppingOrigins,
                m_FailedShoppingResources = m_FailedShoppingResources
            };
            base.Dependency = JobChunkExtensions.ScheduleParallel(buyerJob, m_BuyerQuery, JobHandle.CombineDependencies(base.Dependency, chunkListHandle, jobHandle));
            m_ResourceSystem.AddPrefabsReader(base.Dependency);
            m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
            m_PathfindSetupSystem.AddQueueWriter(base.Dependency);
            m_WriteDeps = JobHandle.CombineDependencies(base.Dependency, m_WriteDeps);
            BuyJob buyJob = new BuyJob
            {
                m_Resources = SystemAPI.GetBufferLookup<Game.Economy.Resources>(false),
                m_SalesQueue = m_SalesQueue,
                m_Services = SystemAPI.GetComponentLookup<ServiceAvailable>(false),
                m_TransformDatas = SystemAPI.GetComponentLookup<Game.Objects.Transform>(true),
                m_PropertyRenters = SystemAPI.GetComponentLookup<PropertyRenter>(true),
                m_OwnedVehicles = SystemAPI.GetBufferLookup<OwnedVehicle>(true),
                m_HouseholdCitizens = SystemAPI.GetBufferLookup<HouseholdCitizen>(true),
                m_HouseholdAnimals = SystemAPI.GetBufferLookup<HouseholdAnimal>(true),
                m_Prefabs = SystemAPI.GetComponentLookup<PrefabRef>(true),
                m_ServiceCompanies = SystemAPI.GetComponentLookup<ServiceCompanyData>(true),
                m_Storages = SystemAPI.GetComponentLookup<Game.Companies.StorageCompany>(true),
                m_Households = SystemAPI.GetComponentLookup<Household>(false),
                m_BuyingCompanies = SystemAPI.GetComponentLookup<BuyingCompany>(false),
                m_ResourceDatas = SystemAPI.GetComponentLookup<ResourceData>(true),
                m_TradeCosts = SystemAPI.GetBufferLookup<TradeCost>(false),
                m_CompanyStatistics = SystemAPI.GetComponentLookup<CompanyStatisticData>(false),
                m_OutsideConnections = SystemAPI.GetComponentLookup<Game.Objects.OutsideConnection>(true),
                m_ResourcePrefabs = m_ResourceSystem.GetPrefabs(),
                m_RandomSeed = RandomSeed.Next(),
                m_FrameIndex = m_SimulationSystem.frameIndex,
                m_PersonalCarSelectData = m_PersonalCarSelectData,
                m_PopulationEntity = m_PopulationQuery.GetSingletonEntity(),
                m_CitizenConsumptionAccumulator = m_CityProductionStatisticSystem.GetCityResourceUsageAccumulator(CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens, out JobHandle accumulatorDeps),
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer()
            };
            base.Dependency = IJobExtensions.Schedule(buyJob, JobHandle.CombineDependencies(base.Dependency, accumulatorDeps));
            m_PersonalCarSelectData.PostUpdate(base.Dependency);
            m_ResourceSystem.AddPrefabsReader(base.Dependency);
            m_CityProductionStatisticSystem.AddCityUsageAccumulatorWriter(CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens, base.Dependency);
            m_EndFrameBarrier.AddJobHandleForProducer(base.Dependency);
        }
    }

}