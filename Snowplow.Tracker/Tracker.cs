﻿/*
 * Tracker.cs
 * 
 * Copyright (c) 2014 Snowplow Analytics Ltd. All rights reserved.
 * This program is licensed to you under the Apache License Version 2.0,
 * and you may not use this file except in compliance with the Apache License
 * Version 2.0. You may obtain a copy of the Apache License Version 2.0 at
 * http://www.apache.org/licenses/LICENSE-2.0.
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the Apache License Version 2.0 is distributed on
 * an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the Apache License Version 2.0 for the specific
 * language governing permissions and limitations there under.
 * Authors: Fred Blundun
 * Copyright: Copyright (c) 2014 Snowplow Analytics Ltd
 * License: Apache License Version 2.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using SelfDescribingJson = System.Collections.Generic.Dictionary<string, object>;
using Context = System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>;
using Snowplow.Tracker.Logging;
using Snowplow.Tracker.Emitters.Endpoints;
using Snowplow.Tracker.Storage;
using Snowplow.Tracker.Queues;
using Snowplow.Tracker.Models.Adapters;

namespace Snowplow.Tracker
{
    public sealed class Tracker
    {
        private static readonly object _createLock = new object();
        private static volatile Tracker _t;

        private readonly object _lock = new object();
        private volatile bool _running = false;

        private Subject _subject;
        private IEmitter _emitter;
        private bool _encodeBase64;
        private IDisposable _storage;
        private Dictionary<string, string> _standardNvPairs;

        private ILogger _logger;

        /// <summary>
        /// The tracker instance
        /// </summary>
        public static Tracker Instance
        {
            get
            {
                lock (_createLock)
                {

                    if (_t == null)
                    {
                        _t = new Tracker();
                    }

                    return _t;
                }
            }
        }

        private Tracker() { }

        /// <summary>
        /// If the tracker is started (open for events)
        /// </summary>
        public bool Started
        {
            get
            {
                lock (_lock)
                {
                    return _running;
                }
            }
        }

        /// <summary>
        /// Start a tracker with a default emitter 
        /// </summary>
        /// <param name="endpoint">Hostname of your collector</param>
        /// <param name="dbPath">A filename/path to store queued events in</param>
        /// <param name="method">The method used to send events to a collector. GET or POST</param>
        /// <param name="subject">Information on the user</param>
        /// <param name="trackerNamespace">Namespace of tracker</param>
        /// <param name="appId">Application ID of tracker</param>
        /// <param name="encodeBase64">Base64 encode collector parameters</param>
        /// <param name="l">A logger to emit an activity stream to</param>
        public void Start(string endpoint, string dbPath, HttpMethod method = HttpMethod.POST, Subject subject = null, string trackerNamespace = null, string appId = null, bool encodeBase64 = true, ILogger l = null)
        {
            AsyncEmitter emitter;
            lock (_lock)
            {
                var dest = new SnowplowHttpCollectorEndpoint(endpoint, method: method, l: l);
                var storage = new LiteDBStorage(dbPath);
                _storage = storage;
                var queue = new PersistentBlockingQueue(storage, new PayloadToJsonString());
                emitter = new AsyncEmitter(dest, queue, l: l);
            }
            Start(emitter, subject, trackerNamespace, appId, encodeBase64, l);
        }

        /// <summary>
        /// Snowplow Tracker class
        /// </summary>
        /// <param name="emitters">List of emitters to which events will be sent</param>
        /// <param name="subject">Subject to be tracked</param>
        /// <param name="trackerNamespace">Identifier for the tracker instance</param>
        /// <param name="appId">Application ID</param>
        /// <param name="encodeBase64">Whether custom event JSONs and custom context JSONs should be base 64 encoded</param>
        public void Start(IEmitter endpoint, Subject subject = null, string trackerNamespace = null, string appId = null, bool encodeBase64 = true, ILogger l = null)
        {
            lock (_lock)
            {
                if (_running)
                {
                    throw new InvalidOperationException("Cannot start - already started");
                }
                _emitter = endpoint;
                _emitter.Start();
                
                _subject = subject ?? new Subject();
                _encodeBase64 = encodeBase64;
                _logger = l ?? new NoLogging();
                _standardNvPairs = new Dictionary<string, string>
                {
                    { "tv", Version.VERSION },
                    { "tna", trackerNamespace },
                    { "aid", appId }
                };
                _running = true;
                _logger.Info("Tracker started");
            }

        }

        /// <summary>
        /// Stop the tracker processing new events
        /// </summary>
        public void Stop()
        {
            lock (_createLock)
            {
                lock (_lock)
                {
                    if (_running)
                    {
                        _t = null;
                        _emitter.Close();
                        _emitter = null;
                        if (_storage != null)
                        {
                            _storage.Dispose();
                        }
                        _storage = null;
                        _running = false;
                        _logger.Info("Tracker stopped");
                        _logger = null;
                    }
                }
            }
        }

        // Setter methods which call the relevant setter methods on the subject

        public Tracker SetPlatform(Platform value)
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _subject.SetPlatform(value);
            }
            return this;
        }

        public Tracker SetUserId(string id)
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _subject.SetUserId(id);
            }
            return this;
        }

        public Tracker SetScreenResolution(int width, int height)
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _subject.SetScreenResolution(width, height);
            }
            return this;
        }

        public Tracker SetViewport(int width, int height)
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _subject.SetViewport(width, height);
            }
            return this;
        }

        public Tracker SetColorDepth(int depth)
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _subject.SetColorDepth(depth);
            }
            return this;
        }

        public Tracker SetTimezone(string timezone)
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _subject.SetTimezone(timezone);
            }
            return this;
        }

        public Tracker SetLang(string lang)
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _subject.SetLang(lang);
            }
            return this;
        }

        /// <summary>
        /// Gets the timestamp for an event
        /// </summary>
        /// <param name="tstamp">A user-provided timestamp or null</param>
        /// <returns>The timestamp for the event</returns>
        private static Int64 GetTimestamp(Int64? tstamp)
        {
            return tstamp ?? (Int64)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalMilliseconds;
        }

        /// <summary>
        /// Inputs an event into each emitter
        /// </summary>
        /// <param name="pb">The event payload</param>
        private void Track(Payload pb)
        {
            if (!_running)
            {
                throw new InvalidOperationException("Cannot track anything - not started");
            }
            _emitter.Input(pb);
        }

        /// <summary>
        /// Called by all tracking events to add the standard name-value pairs, the
        /// subject name-value pairs, the timestamp, and any context to the Payload
        /// </summary>
        /// <param name="pb">Payload to complete</param>
        /// <param name="context">Custom context</param>
        /// <param name="tstamp">User-provided timestamp</param>
        private void CompletePayload(Payload pb, Context context, Int64? tstamp)
        {
            pb.Add("dtm", GetTimestamp(tstamp));
            pb.Add("eid", Guid.NewGuid().ToString());
            if (context != null && context.Any())
            {
                var contextEnvelope = new Dictionary<string, object>
                {
                    { "schema", "iglu:com.snowplowanalytics.snowplow/contexts/jsonschema/1-0-0" },
                    { "data", context }
                };
                pb.AddJson(contextEnvelope, _encodeBase64, "cx", "co");
            }
            pb.AddDict(_standardNvPairs);
            pb.AddDict(_subject.nvPairs);

            Track(pb);
        }

        private void ensureTrackerStarted()
        {
            if (!_running)
            {
                throw new NotSupportedException("Cannot track - tracker is not started. Please use Tracker.Start prior to use.");
            }
        }

        /// <summary>
        /// Track a Snowplow page view event
        /// </summary>
        /// <param name="pageUrl">Page URL</param>
        /// <param name="pageTitle">Page title</param>
        /// <param name="referrer">Page referrer</param>
        /// <param name="context">List of custom contexts for the event</param>
        /// <param name="tstamp">User-provided timestamp for the event</param>
        /// <returns>this</returns>
        public Tracker TrackPageView(string pageUrl, string pageTitle = null, string referrer = null, Context context = null, Int64? tstamp = null)
        {
            lock (_lock)
            {
                ensureTrackerStarted();

                Payload pb = new Payload();
                pb.Add("e", "pv");
                pb.Add("url", pageUrl);
                pb.Add("page", pageTitle);
                pb.Add("refr", referrer);
                CompletePayload(pb, context, tstamp);
            }
            return this;
        }

        /// <summary>
        /// Send an event corresponding to a single item in a transaction
        /// </summary>
        /// <param name="orderId">Unique ID for the whole order</param>
        /// <param name="currency">Currency for the order</param>
        /// <param name="item">TransactionItem object containing data about the item</param>
        /// <param name="context">List of custom contexts for the event</param>
        /// <param name="tstamp">User-provided timestamp for the event</param>
        private void TrackEcommerceTransactionItem(string orderId, string currency, TransactionItem item, Int64? tstamp)
        {
            lock (_lock)
            {
                ensureTrackerStarted();

                Payload pb = new Payload();
                pb.Add("e", "ti");
                pb.Add("ti_id", orderId);
                pb.Add("ti_cu", currency);
                pb.Add("ti_sk", item.sku);
                pb.Add("ti_pr", item.price);
                pb.Add("ti_qu", item.quantity);
                pb.Add("ti_nm", item.name);
                pb.Add("ti_ca", item.category);
                CompletePayload(pb, item.context, tstamp);
            }
        }

        /// <summary>
        /// Track a Snowplow ecommerce transaction event
        /// Fires one event for the whole transaction and one for each item in the transaction
        /// </summary>
        /// <param name="orderId">Unique ID for the whole order</param>
        /// <param name="totalValue">Total transaction value</param>
        /// <param name="affiliation">Transaction affiliation</param>
        /// <param name="taxValue">Transaction tax value</param>
        /// <param name="shipping">Delivery charge</param>
        /// <param name="city">Delivery address city</param>
        /// <param name="state">Delivery address state or province</param>
        /// <param name="country">Delivery address country</param>
        /// <param name="currency">Currency for the order</param>
        /// <param name="items">List of items in the transaction</param>
        /// <param name="context">List of custom contexts for the event</param>
        /// <param name="tstamp">User-provided timestamp for the event</param>
        /// <returns>this</returns>
        public Tracker TrackEcommerceTransaction(string orderId, double totalValue, string affiliation = null, double? taxValue = null, double? shipping = null, string city = null, string state = null, string country = null, string currency = null, List<TransactionItem> items = null, Context context = null, Int64? tstamp = null)
        {
            lock (_lock)
            {
                ensureTrackerStarted();

                Payload pb = new Payload();
                pb.Add("e", "tr");
                pb.Add("tr_id", orderId);
                pb.Add("tr_tt", totalValue);
                pb.Add("tr_af", affiliation);
                pb.Add("tr_tx", taxValue);
                pb.Add("tr_sh", shipping);
                pb.Add("tr_ci", city);
                pb.Add("tr_st", state);
                pb.Add("tr_co", country);
                pb.Add("tr_cu", currency);
                CompletePayload(pb, context, tstamp);

                if (items != null)
                {
                    foreach (TransactionItem item in items)
                    {
                        TrackEcommerceTransactionItem(orderId, currency, item, tstamp);
                    }
                }
            }
            return this;
        }

        /// <summary>
        /// Track a Snowplow structured event
        /// </summary>
        /// <param name="category">Event category</param>
        /// <param name="action">The event itself</param>
        /// <param name="label">The object upon which the action is performed</param>
        /// <param name="property">Property associated with the action or its object</param>
        /// <param name="value">Value associated with the action or its object</param>
        /// <param name="context">List of custom contexts for the event</param>
        /// <param name="tstamp">User-provided timestamp for the event</param>
        /// <returns>this</returns>
        public Tracker TrackStructEvent(string category, string action, string label = null, string property = null, double? value = null, Context context = null, Int64? tstamp = null)
        {
            lock (_lock)
            {
                ensureTrackerStarted();

                Payload pb = new Payload();
                pb.Add("e", "se");
                pb.Add("se_ca", category);
                pb.Add("se_ac", action);
                pb.Add("se_la", label);
                pb.Add("se_pr", property);
                pb.Add("se_va", value);
                CompletePayload(pb, context, tstamp);
            }

            return this;
        }

        /// <summary>
        /// Track a Snowplow custom unstructured event
        /// </summary>
        /// <param name="eventJson">Self-describing JSON for the event</param>
        /// <param name="context">List of custom contexts for the event</param>
        /// <param name="tstamp">User-provided timestamp for the event</param>
        /// <returns>this</returns>
        public Tracker TrackUnstructEvent(SelfDescribingJson eventJson, Context context = null, Int64? tstamp = null)
        {
            lock (_lock)
            {
                ensureTrackerStarted();

                var envelope = new Dictionary<string, object>
                {
                    { "schema", "iglu:com.snowplowanalytics.snowplow/unstruct_event/jsonschema/1-0-0" },
                    { "data", eventJson }
                };
                Payload pb = new Payload();
                pb.Add("e", "ue");
                pb.AddJson(envelope, _encodeBase64, "ue_px", "ue_pr");
                CompletePayload(pb, context, tstamp);
            }

            return this;
        }

        /// <summary>
        /// Track a Snowplow screen view event
        /// </summary>
        /// <param name="name">Name of the screen</param>
        /// <param name="id">Unique ID of the screen</param>
        /// <param name="context">List of custom contexts for the event</param>
        /// <param name="tstamp">User-provided timestamp for the event</param>
        /// <returns>this</returns>
        public Tracker TrackScreenView(string name = null, string id = null, Context context = null, Int64? tstamp = null)
        {
            var screenViewProperties = new Dictionary<string, string>();
            if (name != null)
            {
                screenViewProperties["name"] = name;
            }
            if (id != null)
            {
                screenViewProperties["id"] = id;
            }
            var envelope = new Dictionary<string, object>
            {
                { "schema", "iglu:com.snowplowanalytics.snowplow/screen_view/jsonschema/1-0-0" },
                { "data", screenViewProperties }
            };
            TrackUnstructEvent(envelope, context, tstamp);
            return this;
        }

        /// <summary>
        /// Manually flush all emitters to which the tracker sends events
        /// </summary>
        /// <param name="sync">Whether the flush should be synchronous</param>
        /// <returns>this</returns>
        public Tracker Flush()
        {
            lock (_lock)
            {
                ensureTrackerStarted();
                _emitter.Flush();
            }
            return this;
        }

        /// <summary>
        /// Set the subject of the events fired by the tracker
        /// </summary>
        /// <param name="subject">Subject to track</param>
        /// <returns>this</returns>
        public Tracker SetSubject(Subject subject)
        {
            lock (_lock)
            {
                _subject = subject;
            }
            return this;
        }

    }
}
