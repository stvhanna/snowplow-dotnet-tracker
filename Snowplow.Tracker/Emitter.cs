﻿/*
 * Emitter.cs
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
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Web;

namespace Snowplow.Tracker
{
    public class Emitter
    {
        private string collectorUri;
        private string method;
        private int bufferSize;
        volatile private List<Dictionary<string, string>> buffer;
        private Action<int> onSuccess;
        private Action<int, List<Dictionary<string, string>>> onFailure = null;

        public Emitter(string endpoint, string protocol = "http", int? port = null, string method = "get", int? bufferSize = null, Action<int> onSuccess = null, Action<int, List<Dictionary<string, string>>> onFailure = null)
        {
            collectorUri = getCollectorUri(endpoint, protocol, port, method);
            this.method = method;
            this.buffer = new List<Dictionary<string, string>>();
            this.bufferSize = bufferSize ?? (method == "get" ? 0 : 10);
            this.onSuccess = onSuccess;
            this.onFailure = onFailure;
        }

        private static string getCollectorUri(string endpoint, string protocol, int? port, string method)
        {
            string path;
            if (method == "get")
            {
                path = "/i";
            }
            else
            {
                path = "/com.snowplowanalytics.snowplow/tp2";
            }
            if (port == null)
            {
                return protocol + "://" + endpoint + path;
            }
            else
            {
                return protocol + "://" + port.ToString() + path;
            }
        }

        public void input(Dictionary<string, string> payload)
        {
            buffer.Add(payload);
            if (buffer.Count >= bufferSize)
            {
                flush();
            }
        }

        virtual public void flush(bool sync = false)
        {
            sendRequests();
        }

        protected void sendRequests()
        {
            if (method == "get")
            {
                int successCount = 0;
                var unsentRequests = new List<Dictionary<string, string>>();
                while (buffer.Count > 0)
                {
                    var payload = buffer[0];
                    buffer.RemoveAt(0);
                    string statusCode = httpGet(payload).StatusCode.ToString();
                    if (statusCode == "OK")
                    {
                        successCount += 1;
                    }
                    else
                    {
                        unsentRequests.Add(payload);
                    }
                }
                if (unsentRequests.Count == 0)
                {
                    if (onSuccess != null)
                    {
                        onSuccess(successCount);
                    }
                }
                else
                {
                    if (onFailure != null)
                    {
                        onFailure(successCount, unsentRequests);
                    }
                }
            }
        }

        private static string ToQueryString(Dictionary<string, string> payload)
        {
            var array = (from key in payload.Keys
                         select string.Format("{0}={1}", HttpUtility.UrlEncode(key), HttpUtility.UrlEncode(payload[key])))
                .ToArray();
            return "?" + string.Join("&", array);
        }

        private HttpWebResponse httpGet(Dictionary<string, string> payload)
        {
            string destination = collectorUri + ToQueryString(payload);
            Console.WriteLine("DESTINATION: " + destination); // TODO remove debug code
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(destination);
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            if (response != null)
            {
                Console.WriteLine(response.StatusCode);
                Console.WriteLine(response.ResponseUri);
            }
            return response;
        }

    }
}