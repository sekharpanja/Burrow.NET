﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Burrow.Internal;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Util;

namespace Burrow
{
    /// <summary>
    /// Inherit from <see cref="QueueingBasicConsumer"/> to handle message using a <see cref="IMessageHandler"/>
    /// </summary>
    public class BurrowConsumer : QueueingBasicConsumer, IDisposable
    {
        protected readonly IRabbitWatcher _watcher;
        private readonly bool _autoAck;
        private bool _channelShutdown;

        private readonly object _sharedQueueLock = new object();
        /// <summary>
        /// Control the sharedqueue to receive enough messages to process in parallel if <see cref="BatchSize"/> greater than 1
        /// </summary>
        protected SafeSemaphore _pool { get; private set; }
        private readonly IMessageHandler _messageHandler;

        private int _messagesInProgressCount;

        /// <summary>
        /// Initialize an object of <see cref="BurrowConsumer"/>
        /// </summary>
        /// <param name="channel">RabbitMQ.Client channel</param>
        /// <param name="messageHandler">An instance of message handler to handle the message from queue</param>
        /// <param name="watcher"></param>
        /// <param name="autoAck">If set to true, the msg will be acked after processed</param>
        /// <param name="batchSize"></param>
        public BurrowConsumer(IModel channel,
                              IMessageHandler messageHandler,
                              IRabbitWatcher watcher,

                              bool autoAck,
                              int batchSize)
            : base(channel, new SharedQueue())
        {
            if (channel == null)
            {
                throw new ArgumentNullException("channel");
            }
            if (messageHandler == null)
            {
                throw new ArgumentNullException("messageHandler");
            }
            if (watcher == null)
            {
                throw new ArgumentNullException("watcher");
            }

            if (batchSize < 1)
            {
                throw new ArgumentNullException("batchSize", "batchSize must be greater than or equal 1");
            }

            Model.ModelShutdown += WhenChannelShutdown;
            Model.BasicRecoverAsync(true);
            BatchSize = batchSize;

            _pool = new SafeSemaphore(watcher, BatchSize, BatchSize);
            _watcher = watcher;
            _autoAck = autoAck;


            _messageHandler = messageHandler;
            _messageHandler.HandlingComplete += MessageHandlerHandlingComplete;
            _messageHandler.MessageWasNotHandled += MessageWasNotHandled;
            
            Task.Factory.StartNew(() =>
            {
                try
                {
                    Thread.CurrentThread.Name = string.Format("Consumer thread: {0}", ConsumerTag);
                    while (!_disposed && !_channelShutdown)
                    {
                        WaitAndHandleMessageDelivery();
                    }
                }
                catch (ThreadStateException tse)
                {
                    _watcher.WarnFormat("The consumer thread {0} got a ThreadStateException: {1}, {2}", ConsumerTag, tse.Message, tse.StackTrace);
                }
                catch (ThreadInterruptedException)
                {
                    _watcher.WarnFormat("The consumer thread {0} is interrupted", ConsumerTag);
                }
                catch (ThreadAbortException)
                {
                    _watcher.WarnFormat("The consumer thread {0} is aborted", ConsumerTag);
                }
            }, TaskCreationOptions.LongRunning);
        }

        internal virtual void WaitAndHandleMessageDelivery()
        {
            try
            {
                BasicDeliverEventArgs deliverEventArgs = null;
                lock (_sharedQueueLock)
                {
#if DEBUG
                    _watcher.DebugFormat("1. Wait the semaphore to release");
                    _pool.WaitOne();
                    _watcher.DebugFormat("2. Semaphore released, wait a msg from RabbitMQ. Probably a wait-for-ack message is blocking this");
#else
                    _pool.WaitOne();
#endif
                    if (!_disposed)
                    {
                        deliverEventArgs = Queue.Dequeue() as BasicDeliverEventArgs;
                    }
#if DEBUG

                    _watcher.DebugFormat("3. Msg from RabbitMQ arrived (probably the previous msg has been acknownledged), prepare to handle it");
#endif
                }
                if (deliverEventArgs != null)
                {
                    HandleMessageDelivery(deliverEventArgs);
                }
                else
                {
                    _watcher.ErrorFormat("Message arrived but it's not a BasicDeliverEventArgs for some reason, properly a serious BUG :D, contact author asap, release semaphore for other messages");
                    _pool.Release();
                }
            }
            catch (EndOfStreamException)
            {
                // This thread will be ended soon because the new consumer will be created
                // do nothing here, EOS fired when queue is closed
                // Looks like the connection has gone away, so wait a little while
                // before continuing to poll the queue
                Thread.Sleep(100);
#if DEBUG
                _watcher.DebugFormat("EndOfStreamException occurs, release the semaphore for another message");

#endif
                _pool.Release();
            }
            catch (BadMessageHandlerException ex)
            {
                _watcher.Error(ex);
                Dispose();
            }
        }

        private void MessageWasNotHandled(BasicDeliverEventArgs eventArgs)
        {
            try
            {
                if (!_autoAck && !_disposed)
                {
                    DoAck(eventArgs, this);
                }
            }
            catch(Exception ex)
            {
                _watcher.Error(ex);
            }
        }


        private void MessageHandlerHandlingComplete(BasicDeliverEventArgs eventArgs)
        {
            try
            {
                if (_autoAck && !_disposed)
                {
#if DEBUG
                    _watcher.DebugFormat("7. A task to execute the provided callback with DTag: {0} by CTag: {1} has been finished, now ack message", eventArgs.DeliveryTag, eventArgs.ConsumerTag);

#endif
                    DoAck(eventArgs, this);
                }
            }
            catch (Exception ex)
            {
                _watcher.Error(ex);
            }
            finally
            {
#if DEBUG
                _watcher.DebugFormat("6. A task to execute the provided callback with DTag: {0} by CTag: {1} has been finished, now release the semaphore", eventArgs.DeliveryTag, eventArgs.ConsumerTag);

#endif
                _pool.Release();
                Interlocked.Decrement(ref _messagesInProgressCount);
            }
        }

        protected virtual void WhenChannelShutdown(IModel model, ShutdownEventArgs reason)
        {
            Queue.Close();
            _channelShutdown = true;
            _watcher.WarnFormat("Channel on queue {0} is shutdown: {1}", ConsumerTag, reason.ReplyText);
        }

        private void HandleMessageDelivery(BasicDeliverEventArgs basicDeliverEventArgs)
        {
            try
            {
                if (_watcher.IsDebugEnable)
                {
                    _watcher.DebugFormat("Received CId: {0}, RKey: {1}, DTag: {2}", basicDeliverEventArgs.BasicProperties.CorrelationId, basicDeliverEventArgs.RoutingKey, basicDeliverEventArgs.DeliveryTag);
                }
                //NOTE: We dont have to catch exception here 
                _messageHandler.HandleMessage(basicDeliverEventArgs);
                Interlocked.Increment(ref _messagesInProgressCount);
            }
            catch (Exception ex)
            {
                throw new BadMessageHandlerException(ex);
            }
        }

        protected virtual void DoAck(BasicDeliverEventArgs basicDeliverEventArgs, IBasicConsumer subscriptionInfo)
        {
            if (_disposed)
            {
                return;
            }

            Subscription.TryAckOrNAck(channel => channel.BasicAck(basicDeliverEventArgs.DeliveryTag, false), subscriptionInfo.Model, _watcher);
        }

        /// <summary>
        /// The number of threads to process messages, Default is Global.DefaultConsumerBatchSize
        /// </summary>
        public int BatchSize { get; private set; }

        private volatile bool _disposed;
        public virtual void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            //NOTE: Wait all current running tasks to finish and after that dispose the objects
            DateTime timeOut = DateTime.Now.AddSeconds(Global.ConsumerDisposeTimeoutInSeconds);
            while (_messagesInProgressCount > 0 && DateTime.Now <= timeOut)
            {
                _watcher.InfoFormat("Wait for {0} messages in progress", _messagesInProgressCount);
                Thread.Sleep(1000);
            }

            _pool.Dispose();
            Queue.Close();
        }
    }
}
