﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Snowplow.Tracker.Models.Adapters;
using Snowplow.Tracker.Queues;
using Snowplow.Tracker.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Snowplow.Tracker.Tests.Queues
{

    class MockStorage : IStorage
    {

        private List<string> _items = new List<string>(); 

        public int TotalItems
        {
            get
            {
               return _items.Count;
            }
        }

        public List<string> TakeLast(int n)
        {
            if (TotalItems - n < 0)
            {
                throw new ArgumentException("insufficient number of records");
            }
            var items = _items.GetRange(TotalItems - n, n);
            _items.RemoveRange(TotalItems - n, n);
            return items;
        }

        public void Put(string item)
        {
            _items.Insert(0, item);
        }
    }

    [TestClass]
    public class PersistentBlockingQueueTest
    {
        [TestMethod]
        public void testMockStorageOne()
        {
            var s = new MockStorage();

            Assert.AreEqual(s.TotalItems, 0);
            s.Put("hello world");
            Assert.AreEqual(1, s.TotalItems);
            var i = s.TakeLast(1);
            Assert.AreEqual(1, i.Count);
            Assert.AreEqual("hello world", i[0]);
            Assert.AreEqual(0, s.TotalItems);
        }

        [TestMethod]
        public void testMockStorageMany()
        {
            var s = new MockStorage();

            Assert.AreEqual(s.TotalItems, 0);
            s.Put("hello world");
            s.Put("hello world 2");
            s.Put("hello world 3");
            Assert.AreEqual(3, s.TotalItems);
            var i = s.TakeLast(3);
            Assert.AreEqual(3, i.Count);
            Assert.AreEqual("hello world", i[2]);
            Assert.AreEqual("hello world 2", i[1]);
            Assert.AreEqual("hello world 3", i[0]);
            Assert.AreEqual(0, s.TotalItems);
        }

        [TestMethod]
        public void testAddRemoveOne()
        {
            var storage = new MockStorage();
            var queue = new PersistentBlockingQueue(storage, new PayloadToJsonString());

            var dict = new Dictionary<string, string>();
            dict.Add("hello", "world");
            var samplePayload = new Payload();
            samplePayload.AddDict(dict);

            var payload = new List<Payload>();
            payload.Add(samplePayload);

            queue.Enqueue(payload);
            var actualPayload = queue.Dequeue();

            Assert.AreEqual(1, actualPayload.Count);
            CollectionAssert.AreEqual(samplePayload.NvPairs, actualPayload[0].NvPairs);
        }

        class MockConsumer
        {
            public List<Payload> Consumed
            {
                get;
                private set;
            }

            private IPersistentBlockingQueue _q;
            private int _count;

            public MockConsumer(int count, IPersistentBlockingQueue q)
            {
                _count = count;
                _q = q;
            }

            public void Consume()
            {
                Consumed = new List<Payload>();

                for (int i = 0; i < _count; i++)
                {
                    Consumed.Add(_q.Dequeue()[0]);
                }
            }
        }

        class MockProducer {

            private IPersistentBlockingQueue _q;
            private int _count;

            public MockProducer(int count, IPersistentBlockingQueue q)
            {
                _count = count;
                _q = q;
            }

            public void Produce()
            {
                for (int i = 0; i < _count; i++) {
                    var dict = new Dictionary<string, string>();
                    dict.Add("hello", "world");
                    var samplePayload = new Payload();
                    samplePayload.AddDict(dict);

                    var payload = new List<Payload>();
                    payload.Add(samplePayload);

                    _q.Enqueue(payload);
                }
            }

        }

        [TestMethod]
        public void testAddRemoveThreaded()
        {
            var mockStorage = new MockStorage();

            var q = new PersistentBlockingQueue(mockStorage, new PayloadToJsonString());

            int expectedRecordCount = 1000;

            var consumer = new MockConsumer(expectedRecordCount, q);

            var producer = new MockProducer(expectedRecordCount / 2, q);
            var secondProducer = new MockProducer(expectedRecordCount / 2, q);

            var consumerThread = new Thread(new ThreadStart(consumer.Consume));
            consumerThread.Start();

            var producerThread = new Thread(new ThreadStart(producer.Produce));
            producerThread.Start();

            var secondProducerThread = new Thread(new ThreadStart(secondProducer.Produce));
            secondProducerThread.Start();

            consumerThread.Join(1000); // time out if errors

            Assert.AreEqual(expectedRecordCount, consumer.Consumed.Count);
        }

        [TestMethod]
        public void testMultipleConsumers()
        {
            var mockStorage = new MockStorage();

            var q = new PersistentBlockingQueue(mockStorage, new PayloadToJsonString());

            int expectedRecordCount = 1000;

            var producer = new MockProducer(expectedRecordCount, q);

            var producerThread = new Thread(new ThreadStart(producer.Produce));
            producerThread.Start();

            var threads = new List<Thread>();
            var consumers = new List<MockConsumer>();

            for (int i = 0; i < expectedRecordCount; i++) {
                var consumer = new MockConsumer(1, q);
                var consumerThread = new Thread(new ThreadStart(consumer.Consume));
                consumerThread.Start();
                threads.Add(consumerThread);
                consumers.Add(consumer);
            }

            threads.ForEach(t => { t.Join(100); });

            var total = (from c in consumers select c.Consumed.Count).Sum();
            bool allConsumedOneItem = !consumers.Any(item => { return item.Consumed.Count != 1; });

            Assert.IsTrue(allConsumedOneItem);
            Assert.AreEqual(expectedRecordCount, total);
        }

    }
}
