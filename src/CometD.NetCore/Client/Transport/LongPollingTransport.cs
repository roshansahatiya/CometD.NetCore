using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CometD.NetCore.Bayeux;
using CometD.NetCore.Common;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace CometD.NetCore.Client.Transport
{
    /// <version>  $Revision$ $Date: 2010-10-19 12:35:37 +0200 (Tue, 19 Oct 2010) $
    /// </version>
    public class LongPollingTransport : HttpClientTransport
    {
        private static ILogger logger = new LoggerFactory()
            .AddConsole(LogLevel.Debug)
            .AddDebug()
            .CreateLogger(nameof(LongPollingTransport));

        private List<TransportExchange> _exchanges = new List<TransportExchange>();
        private List<LongPollingRequest> transportQueue = new List<LongPollingRequest>();
        private HashSet<LongPollingRequest> transmissions = new HashSet<LongPollingRequest>();

        private bool _appendMessageType;

        public LongPollingTransport(IDictionary<string, object> options, NameValueCollection headers)
            : base("long-polling", options, headers)
        {
        }

        public override bool Accept(string bayeuxVersion)
        {
            return true;
        }

        public override void Init()
        {
            base.Init();
            //_aborted = false;
            var uriRegex = new Regex("(^https?://(([^:/\\?#]+)(:(\\d+))?))?([^\\?#]*)(.*)?");
            var uriMatch = uriRegex.Match(Url);
            if (uriMatch.Success)
            {
                var afterPath = uriMatch.Groups[7].ToString();
                _appendMessageType = afterPath == null || afterPath.Trim().Length == 0;
            }
        }

        public override void Abort()
        {
            //_aborted = true;
            lock (this)
            {
                foreach (var exchange in _exchanges)
                {
                    exchange.Abort();
                }

                _exchanges.Clear();
            }
        }

        public override void Reset()
        {
        }

        // Fix for not running more than two simultaneous requests:
        public class LongPollingRequest
        {
            private readonly ITransportListener listener;
            private readonly IList<IMutableMessage> messages;
            private readonly HttpWebRequest request;

            public  TransportExchange Exchange;

            public LongPollingRequest(ITransportListener _listener, IList<IMutableMessage> _messages,
                    HttpWebRequest _request)
            {
                listener = _listener;
                messages = _messages;
                request = _request;
            }

            public void Send()
            {
                try
                {
                    request.BeginGetRequestStream(new AsyncCallback(GetRequestStreamCallback), Exchange);
                }
                catch (Exception e)
                {
                    Exchange.Dispose();
                    listener.OnException(e, ObjectConverter.ToListOfIMessage(messages));
                }
            }
        }


        private void PerformNextRequest()
        {
            var ok = false;
            LongPollingRequest nextRequest = null;

            lock (this)
            {
                if (transportQueue.Count > 0 && transmissions.Count <= 1)
                {
                    ok = true;
                    nextRequest = transportQueue[0];
                    transportQueue.Remove(nextRequest);
                    transmissions.Add(nextRequest);
                }
            }

            if (ok && nextRequest != null)
            {
                nextRequest.Send();
            }
        }

        public void AddRequest(LongPollingRequest request)
        {
            lock (this)
            {
                transportQueue.Add(request);
            }

            PerformNextRequest();
        }

        public void RemoveRequest(LongPollingRequest request)
        {
            lock (this)
            {
                transmissions.Remove(request);
            }

            PerformNextRequest();
        }

        public override void Send(ITransportListener listener, IList<IMutableMessage> messages)
        {
            //Console.WriteLine();
            //Console.WriteLine("send({0} message(s))", messages.Count);
            var url = Url;

            if (_appendMessageType && messages.Count == 1 && messages[0].Meta)
            {
                var type = messages[0].Channel.Substring(ChannelFields.META.Length);
                if (url.EndsWith("/"))
                {
                    url = url.Substring(0, url.Length - 1);
                }

                url += type;
            }

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json;charset=UTF-8";

            if (request.CookieContainer == null)
            {
                request.CookieContainer = new CookieContainer();
            }

            request.CookieContainer.Add(GetCookieCollection());

            if (request.Headers == null)
            {
                request.Headers = new WebHeaderCollection();
            }

            request.Headers.Add(GetHeaderCollection());

            var content = JsonConvert.SerializeObject(ObjectConverter.ToListOfDictionary(messages));

            logger.LogDebug($"Send: {content}");

            var longPollingRequest = new LongPollingRequest(listener, messages, request);

            var exchange = new TransportExchange(this, listener, messages, longPollingRequest)
            {
                Content = content,
                Request = request
            };
            lock (this)
            {
                _exchanges.Add(exchange);
            }

            longPollingRequest.Exchange = exchange;
            AddRequest(longPollingRequest);
        }

        public override bool IsSending
        {
            get
            {
                lock (this)
                {
                    if (transportQueue.Count > 0)
                    {
                        return true;
                    }

                    foreach (var transmission in transmissions)
                    {
                        if (transmission.Exchange.IsSending)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }
        }

        // From http://msdn.microsoft.com/en-us/library/system.net.httpwebrequest.begingetrequeststream.aspx
        private static void GetRequestStreamCallback(IAsyncResult asynchronousResult)
        {
            var exchange = (TransportExchange)asynchronousResult.AsyncState;

            try
            {
                // End the operation
                using (var postStream = exchange.Request.EndGetRequestStream(asynchronousResult))
                {
                    // Convert the string into a byte array.
                    var byteArray = Encoding.UTF8.GetBytes(exchange.Content);
                    //Console.WriteLine("Sending message(s): {0}", exchange.content);

                    // Write to the request stream.
                    postStream.Write(byteArray, 0, exchange.Content.Length);
                    postStream.Close();
                }

                // Start the asynchronous operation to get the response
                exchange.Listener.OnSending(ObjectConverter.ToListOfIMessage(exchange.Messages));
                var result = exchange.Request.BeginGetResponse(new AsyncCallback(GetResponseCallback), exchange);

                long timeout = 120000;
                ThreadPool.RegisterWaitForSingleObject(result.AsyncWaitHandle, new WaitOrTimerCallback(TimeoutCallback), exchange, timeout, true);

                exchange.IsSending = false;
            }
            catch (Exception e)
            {
                if (exchange.Request != null)
                {
                    exchange.Request.Abort();
                }

                exchange.Dispose();
                exchange.Listener.OnException(e, ObjectConverter.ToListOfIMessage(exchange.Messages));
            }
        }

        private static void GetResponseCallback(IAsyncResult asynchronousResult)
        {
            var exchange = (TransportExchange)asynchronousResult.AsyncState;

            try
            {
                // End the operation
                string responsestring;
                using (var response = (HttpWebResponse)exchange.Request.EndGetResponse(asynchronousResult))
                {
                    using (var streamResponse = response.GetResponseStream())
                    {
                        using (var streamRead = new StreamReader(streamResponse))
                        {
                            responsestring = streamRead.ReadToEnd();
                        }
                    }
                    //Console.WriteLine("Received message(s): {0}", responsestring);

                    if (response.Cookies != null)
                    {
                        foreach (Cookie cookie in response.Cookies)
                        {
                            exchange.AddCookie(cookie);
                        }
                    }

                    response.Close();
                }
                exchange.Messages = DictionaryMessage.ParseMessages(responsestring);

                exchange.Listener.OnMessages(exchange.Messages);
                exchange.Dispose();
            }
            catch (Exception e)
            {
                exchange.Listener.OnException(e, ObjectConverter.ToListOfIMessage(exchange.Messages));
                exchange.Dispose();
            }
        }

        // From http://msdn.microsoft.com/en-us/library/system.net.httpwebrequest.begingetresponse.aspx
        // Abort the request if the timer fires.
        private static void TimeoutCallback(object state, bool timedOut)
        {
            if (timedOut)
            {
                Console.WriteLine("Timeout");
                var exchange = state as TransportExchange;

                if (exchange.Request != null)
                {
                    exchange.Request.Abort();
                }
                exchange.Dispose();
            }
        }

        public class TransportExchange
        {
            private readonly LongPollingTransport parent;

            public string Content;
            public HttpWebRequest Request;
            public ITransportListener Listener;
            public IList<IMutableMessage> Messages;
            public LongPollingRequest LongPollingRequest;
            public bool IsSending;

            public TransportExchange(LongPollingTransport parent,
                ITransportListener listener,
                IList<IMutableMessage> messages,
                LongPollingRequest _lprequest)
            {
                this.parent = parent;
                Listener = listener;
                Messages = messages;
                Request = null;
                LongPollingRequest = _lprequest;
                IsSending = true;
            }

            public void AddCookie(Cookie cookie)
            {
                parent.AddCookie(cookie);
            }

            public void Dispose()
            {
                parent.RemoveRequest(LongPollingRequest);
                lock (parent)
                {
                    parent._exchanges.Remove(this);
                }
            }

            public void Abort()
            {
                if (Request != null)
                {
                    Request.Abort();
                }
            }
        }
    }
}