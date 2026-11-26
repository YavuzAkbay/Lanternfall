using System;
using System.Net;

namespace Selene
{
    // A WebClient that gives up after a while: the plain one waits 100 s for the headers and 5 minutes for a stalled body,
    // which parks a library-wide worker thread (or a lock) for that long. Downloads that announce a size above MaxBytes are refused.
    internal class TimedWebClient : WebClient
    {
        public int TimeoutMs = 15000;
        public long MaxBytes = 64L * 1024 * 1024;

        protected override WebRequest GetWebRequest(Uri address)
        {
            var request = base.GetWebRequest(address);
            if (request != null)
            {
                request.Timeout = TimeoutMs;
                var http = request as HttpWebRequest;
                if (http != null) http.ReadWriteTimeout = TimeoutMs;
            }
            return request;
        }

        protected override WebResponse GetWebResponse(WebRequest request)
        {
            var response = base.GetWebResponse(request);
            if (response != null && response.ContentLength > MaxBytes)
            {
                response.Close();
                throw new WebException("The download is larger than " + (MaxBytes / (1024 * 1024)) + " MB");
            }
            return response;
        }

        protected override WebResponse GetWebResponse(WebRequest request, IAsyncResult result)
        {
            var response = base.GetWebResponse(request, result);
            if (response != null && response.ContentLength > MaxBytes)
            {
                response.Close();
                throw new WebException("The download is larger than " + (MaxBytes / (1024 * 1024)) + " MB");
            }
            return response;
        }
    }
}
