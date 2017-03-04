using System;
using System.Collections.Generic;
using Shuttle.Core.Infrastructure;

namespace Shuttle.Esb
{
	public class ServiceBus : IServiceBus
	{
		private readonly IServiceBusConfiguration _configuration;
		private readonly IMessageSender _messageSender;
		private readonly IPipelineFactory _pipelineFactory;

		private IProcessorThreadPool _controlThreadPool;
		private IProcessorThreadPool _deferredMessageThreadPool;
		private IProcessorThreadPool _inboxThreadPool;
		private IProcessorThreadPool _outboxThreadPool;

		public ServiceBus(IServiceBusConfiguration configuration, ITransportMessageFactory transportMessageFactory,
			IPipelineFactory pipelineFactory, ISubscriptionManager subscriptionManager)
		{
			Guard.AgainstNull(configuration, "configuration");
			Guard.AgainstNull(transportMessageFactory, "transportMessageFactory");
			Guard.AgainstNull(pipelineFactory, "pipelineFactory");
			Guard.AgainstNull(subscriptionManager, "subscriptionManager");

			_configuration = configuration;
			_pipelineFactory = pipelineFactory;

			_messageSender = new MessageSender(transportMessageFactory, _pipelineFactory, subscriptionManager);
		}

		public IServiceBus Start()
		{
			if (Started)
			{
				throw new ApplicationException(EsbResources.ServiceBusInstanceAlreadyStarted);
			}

			ConfigurationInvariant();

			var startupPipeline = _pipelineFactory.GetPipeline<StartupPipeline>();

			startupPipeline.Execute();

			_inboxThreadPool = startupPipeline.State.Get<IProcessorThreadPool>("InboxThreadPool");
			_controlThreadPool = startupPipeline.State.Get<IProcessorThreadPool>("ControlInboxThreadPool");
			_outboxThreadPool = startupPipeline.State.Get<IProcessorThreadPool>("OutboxThreadPool");
			_deferredMessageThreadPool = startupPipeline.State.Get<IProcessorThreadPool>("DeferredMessageThreadPool");

			Started = true;

			return this;
		}

		public void Stop()
		{
			if (!Started)
			{
				return;
			}

			if (_configuration.HasInbox)
			{
				if (_configuration.Inbox.HasDeferredQueue)
				{
					_deferredMessageThreadPool.Dispose();
				}

				_inboxThreadPool.Dispose();
			}

			if (_configuration.HasControlInbox)
			{
				_controlThreadPool.Dispose();
			}

			if (_configuration.HasOutbox)
			{
				_outboxThreadPool.Dispose();
			}

			Started = false;
		}

		public bool Started { get; private set; }

		public void Dispose()
		{
			Stop();
		}

		public void Dispatch(TransportMessage transportMessage)
		{
			_messageSender.Dispatch(transportMessage);
		}

		public TransportMessage Send(object message)
		{
			return _messageSender.Send(message);
		}

		public TransportMessage Send(object message, Action<TransportMessageConfigurator> configure)
		{
			return _messageSender.Send(message, configure);
		}

		public IEnumerable<TransportMessage> Publish(object message)
		{
			return _messageSender.Publish(message);
		}

		public IEnumerable<TransportMessage> Publish(object message, Action<TransportMessageConfigurator> configure)
		{
			return _messageSender.Publish(message, configure);
		}

		private void ConfigurationInvariant()
		{
			Guard.Against<WorkerException>(_configuration.IsWorker && !_configuration.HasInbox,
				EsbResources.WorkerRequiresInboxException);

			if (_configuration.HasInbox)
			{
				Guard.Against<EsbConfigurationException>(
					_configuration.Inbox.WorkQueue == null && string.IsNullOrEmpty(_configuration.Inbox.WorkQueueUri),
					string.Format(EsbResources.RequiredQueueUriMissing, "Inbox.WorkQueueUri"));

				Guard.Against<EsbConfigurationException>(
					_configuration.Inbox.ErrorQueue == null && string.IsNullOrEmpty(_configuration.Inbox.ErrorQueueUri),
					string.Format(EsbResources.RequiredQueueUriMissing, "Inbox.ErrorQueueUri"));
			}

			if (_configuration.HasOutbox)
			{
				Guard.Against<EsbConfigurationException>(
					_configuration.Outbox.WorkQueue == null && string.IsNullOrEmpty(_configuration.Outbox.WorkQueueUri),
					string.Format(EsbResources.RequiredQueueUriMissing, "Outbox.WorkQueueUri"));

				Guard.Against<EsbConfigurationException>(
					_configuration.Outbox.ErrorQueue == null &&
					string.IsNullOrEmpty(_configuration.Outbox.ErrorQueueUri),
					string.Format(EsbResources.RequiredQueueUriMissing, "Outbox.ErrorQueueUri"));
			}

			if (_configuration.HasControlInbox)
			{
				Guard.Against<EsbConfigurationException>(
					_configuration.ControlInbox.WorkQueue == null &&
					string.IsNullOrEmpty(_configuration.ControlInbox.WorkQueueUri),
					string.Format(EsbResources.RequiredQueueUriMissing, "ControlInbox.WorkQueueUri"));

				Guard.Against<EsbConfigurationException>(
					_configuration.ControlInbox.ErrorQueue == null &&
					string.IsNullOrEmpty(_configuration.ControlInbox.ErrorQueueUri),
					string.Format(EsbResources.RequiredQueueUriMissing, "ControlInbox.ErrorQueueUri"));
			}
		}

		public static IServiceBusConfiguration RegisterComponents(IComponentRegistry registry)
		{
			Guard.AgainstNull(registry, "registry");

			var configuration = new ServiceBusConfiguration();

			new CoreConfigurator().Apply(configuration);
			new UriResolverConfigurator().Apply(configuration);
			new QueueManagerConfigurator().Apply(configuration);
			new MessageRouteConfigurator().Apply(configuration);
			new ControlInboxConfigurator().Apply(configuration);
			new InboxConfigurator().Apply(configuration);
			new OutboxConfigurator().Apply(configuration);
			new WorkerConfigurator().Apply(configuration);

			RegisterComponents(registry, configuration);

			return configuration;
		}

		public static void RegisterComponents(IComponentRegistry registry, IServiceBusConfiguration configuration)
		{
			Guard.AgainstNull(registry, "registry");
			Guard.AgainstNull(configuration, "configuration");

			Register(registry, configuration);

			Register<IServiceBusEvents, ServiceBusEvents>(registry);
			Register<ISerializer, DefaultSerializer>(registry);
			Register<IServiceBusPolicy, DefaultServiceBusPolicy>(registry);
			Register<IMessageRouteProvider, DefaultMessageRouteProvider>(registry);
			Register<IIdentityProvider, DefaultIdentityProvider>(registry);
			Register<IMessageHandlerInvoker, DefaultMessageHandlerInvoker>(registry);
			Register<IMessageHandlingAssessor, DefaultMessageHandlingAssessor>(registry);
			Register<IUriResolver, DefaultUriResolver>(registry);
			Register<IQueueManager, QueueManager>(registry);
			Register<IWorkerAvailabilityManager, WorkerAvailabilityManager>(registry);
			Register<ISubscriptionManager, NullSubscriptionManager>(registry);
			Register<IIdempotenceService, NullIdempotenceService>(registry);

			Register<TransactionScopeObserver, TransactionScopeObserver>(registry);

			if (!registry.IsRegistered<ITransactionScopeFactory>())
			{
				var transactionScopeConfiguration = configuration.TransactionScope ?? new TransactionScopeConfiguration();

				Register<ITransactionScopeFactory>(registry,
					new DefaultTransactionScopeFactory(transactionScopeConfiguration.Enabled,
						transactionScopeConfiguration.IsolationLevel,
						TimeSpan.FromSeconds(transactionScopeConfiguration.TimeoutSeconds)));
			}

			Register<IPipelineFactory, DefaultPipelineFactory>(registry);
			Register<ITransportMessageFactory, DefaultTransportMessageFactory>(registry);

			var reflectionService = new ReflectionService();

			foreach (var type in reflectionService.GetTypes<IPipeline>(typeof(ServiceBus).Assembly))
			{
				if (type.IsInterface || registry.IsRegistered(type))
				{
					continue;
				}

				registry.Register(type, type, Lifestyle.Transient);
			}

			foreach (var type in reflectionService.GetTypes<IPipelineObserver>(typeof(ServiceBus).Assembly))
			{
				if (type.IsInterface || registry.IsRegistered(type))
				{
					continue;
				}

				registry.Register(type, type, Lifestyle.Singleton);
			}

			if (configuration.RegisterHandlers)
			{
				registry.RegisterMessageHandlers();
			}

			var queueFactoryType = typeof(IQueueFactory);
			var queueFactoryImplementationTypes = new List<Type>();

			Action<Type> addQueueFactoryImplementationType = type =>
			{
				if (queueFactoryImplementationTypes.Contains(type))
				{
					return;
				}

				queueFactoryImplementationTypes.Add(type);
			};

			if (configuration.ScanForQueueFactories)
			{
				foreach (var type in new ReflectionService().GetTypes<IQueueFactory>())
				{
					addQueueFactoryImplementationType(type);
				}
			}

			foreach (var type in configuration.QueueFactoryTypes)
			{
				addQueueFactoryImplementationType(type);
			}

			registry.RegisterCollection(queueFactoryType, queueFactoryImplementationTypes, Lifestyle.Singleton);

			registry.Register<IServiceBus, ServiceBus>();
		}

		private static void Register<TDependency, TImplementation>(IComponentRegistry registry)
			where TDependency : class
			where TImplementation : class, TDependency
		{
			if (registry.IsRegistered(typeof(TDependency)))
			{
				return;
			}

			registry.Register<TDependency, TImplementation>();
		}

		private static void Register<TDependency>(IComponentRegistry registry, TDependency instance)
			where TDependency : class
		{
			if (registry.IsRegistered(typeof(TDependency)))
			{
				return;
			}

			registry.Register(instance);
		}

		public static IServiceBus Create(IComponentResolver resolver)
		{
			Guard.AgainstNull(resolver, "resolver");

			var configuration = resolver.Resolve<IServiceBusConfiguration>();

			if (configuration == null)
			{
				throw new InvalidOperationException(string.Format(InfrastructureResources.TypeNotRegisteredException,
					typeof(IServiceBusConfiguration).FullName));
			}

			configuration.Assign(resolver);

			var defaultPipelineFactory = resolver.Resolve<IPipelineFactory>() as DefaultPipelineFactory;

			if (defaultPipelineFactory != null)
			{
				defaultPipelineFactory.Assign(resolver);
			}

			return resolver.Resolve<IServiceBus>();
		}
	}
}