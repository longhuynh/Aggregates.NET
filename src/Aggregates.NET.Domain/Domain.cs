﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using Aggregates.Internal;
using Aggregates.Messages;
using NServiceBus;
using NServiceBus.Features;
using NServiceBus.Logging;
using NServiceBus.Settings;

namespace Aggregates
{
    public class Domain : NServiceBus.Features.Feature
    {
        public Domain()
        {
            Defaults(s =>
            {
                s.SetDefault("ShouldCacheEntities", false);
                s.SetDefault("MaxConflictResolves", 3);
                s.SetDefault("UseNsbForOob", false);
            });
            DependsOn<Aggregates.ConsumerFeature>();
        }
        protected override void Setup(FeatureConfigurationContext context)
        {
            context.RegisterStartupTask(builder => new DomainStart(builder.Build<ISnapshotReader>(), context.Settings));

            var settings = context.Settings;
            context.Container.ConfigureComponent(b => 
                new StoreStreams(b.Build<IStoreEvents>(), b.Build<ICache>(), settings.Get<bool>("ShouldCacheEntities"), settings.Get<StreamIdGenerator>("StreamGenerator")),
                DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent(b =>
                new StoreSnapshots(b.Build<IStoreEvents>(), b.Build<ISnapshotReader>(), settings.Get<StreamIdGenerator>("StreamGenerator")),
                DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent(b =>
                new StorePocos(b.Build<IStoreEvents>(), b.Build<ICache>(), settings.Get<bool>("ShouldCacheEntities"), settings.Get<StreamIdGenerator>("StreamGenerator")),
                DependencyLifecycle.InstancePerCall);

            if (settings.Get<bool>("UseNsbForOob"))
            {
                context.Container.ConfigureComponent<NsbOobHandler>(DependencyLifecycle.InstancePerCall);
            }
            else
            {
                context.Container.ConfigureComponent(b =>
                        new DefaultOobHandler(b.Build<IStoreEvents>(),
                            settings.Get<StreamIdGenerator>("StreamGenerator")),
                    DependencyLifecycle.InstancePerCall);
            }
            context.Container.ConfigureComponent<UnitOfWork>(DependencyLifecycle.InstancePerUnitOfWork);
            context.Container.ConfigureComponent<DefaultRepositoryFactory>(DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent<DefaultRouteResolver>(DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent<Processor>(DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent<ResolveStronglyConflictResolver>(DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent<ResolveWeaklyConflictResolver>(DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent<DiscardConflictResolver>(DependencyLifecycle.InstancePerCall);
            context.Container.ConfigureComponent<IgnoreConflictResolver>(DependencyLifecycle.InstancePerCall);

            context.Container.ConfigureComponent<Func<Accept>>(y =>
            {
                var eventFactory = y.Build<IMessageCreator>();
                return () => eventFactory.CreateInstance<Accept>();
            }, DependencyLifecycle.SingleInstance);

            context.Container.ConfigureComponent<Func<string, Reject>>(y =>
            {
                var eventFactory = y.Build<IMessageCreator>();
                return message => { return eventFactory.CreateInstance<Reject>(e => { e.Message = message; }); };
            }, DependencyLifecycle.SingleInstance);
            context.Container.ConfigureComponent<Func<BusinessException, Reject>>(y =>
            {
                var eventFactory = y.Build<IMessageCreator>();
                return exception => {
                    return eventFactory.CreateInstance<Reject>(e => {
                        e.Message = "Exception raised";
                    });
                };
            }, DependencyLifecycle.SingleInstance);
            
            context.Pipeline.Register<MutateIncomingCommandRegistration>();
            context.Pipeline.Register<CommandAcceptorRegistration>();
            

            //context.Pipeline.Register<SafetyNetRegistration>();
            //context.Pipeline.Register<TesterBehaviorRegistration>();
        }

        public class DomainStart : FeatureStartupTask, IDisposable
        {
            private static readonly ILog Logger = LogManager.GetLogger("DomainStart");

            private readonly ReadOnlySettings _settings;
            private readonly ISnapshotReader _subscriber;
            private readonly CancellationTokenSource _cancellationTokenSource;

            public DomainStart(ISnapshotReader subscriber, ReadOnlySettings settings)
            {
                _subscriber = subscriber;
                _settings = settings;
                _cancellationTokenSource = new CancellationTokenSource();
            }

            protected override async Task OnStart(IMessageSession session)
            {
                Logger.Write(LogLevel.Info, "Starting domain");
                await session.Publish<DomainAlive>(x =>
                {
                    x.Endpoint = _settings.InstanceSpecificQueue();
                    x.Instance = Aggregates.Defaults.Instance;
                }).ConfigureAwait(false);

                Logger.Write(LogLevel.Info, "Starting snapshot consumer");
                await _subscriber.Setup(_settings.EndpointName()).ConfigureAwait(false);

                await _subscriber.Subscribe(_cancellationTokenSource.Token).ConfigureAwait(false);

            }
            protected override async Task OnStop(IMessageSession session)
            {
                Logger.Write(LogLevel.Info, "Stopping domain");
                await session.Publish<DomainDead>(x =>
                {
                    x.Endpoint = _settings.InstanceSpecificQueue();
                    x.Instance = Aggregates.Defaults.Instance;
                }).ConfigureAwait(false);
                Logger.Write(LogLevel.Info, "Stopping snapshot consumer");
                _cancellationTokenSource.Cancel();
            }
            public void Dispose()
            {
                _subscriber.Dispose();
            }
        }
    }
}