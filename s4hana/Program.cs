using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.IO;
using System.Xml;
using Newtonsoft.Json;
using System.Collections.Specialized;
using CsvHelper;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace s4hana
{

    public class Settings
    {
        public class Api
        {
            public string name;
            public string api;
            public string filter;
            public string lookup_api;
            public Dictionary<string, string> fields;
        }
        public string name;
        public string url;
        public Dictionary<string, string> authentication;
        public string[] default_output;
        public Api[] apis;
    }

    public class Response
    {
        private string responseJSON;
        private dynamic[] d_results;
        private Dictionary<int, Response> lookup_Response = new Dictionary<int, Response>();
        public Response(string json)
        {
            responseJSON = json;
        }

        private void GetResults()
        {
            var d = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, dynamic[]>>>(responseJSON);
            d_results = d["d"]["results"];
        }
        public int GetNumResults()
        {
            if (d_results == null)
                GetResults();
            return d_results.Length;
        }
        public IDictionary<string, JToken> GetNthResult(int i)
        {
            try
            {
                if (d_results == null)
                    GetResults();
                return d_results[i];
            }
            catch(Exception e)
            {
                Console.WriteLine(e.StackTrace);
                return null;
            }
        }
        public Response GetNthLookupResponse(int i)
        {
            Response res;
            if (lookup_Response.TryGetValue(i, out res))
            {
                return res;
            }
            return null;
        }
        public void SetNthLookupResponse(int i, Response res)
        {
            lookup_Response.Add(i, res);
        }
        private string GetValue(string key, IDictionary<string, JToken> keyValues, IDictionary<string, JToken> moreKeyValues)
        {
            JToken value;
            if (!keyValues.TryGetValue(key, out value))
            {
                if (moreKeyValues == null)
                    return "";
                if (!moreKeyValues.TryGetValue(key, out value))
                    return "";
            }
            return value.Value<string>();
        }
        public void Serialize(string fileName, string[] columns, Dictionary<string, string> fields)
        {
            using (var writer = new StreamWriter("output.csv"))
            using (var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture))
            {
                foreach (var column in columns)
                {
                    csvWriter.WriteField(column);
                }
                csvWriter.NextRecord();
                csvWriter.Flush();
                for (var i = 0; i < GetNumResults(); i++)
                {
                    IDictionary<string, JToken> result = GetNthResult(i);
                    Response res = GetNthLookupResponse(i);
                    if (res != null)
                    {
                        for (var j = 0; j < res.GetNumResults(); j++)
                        {
                            IDictionary<string, JToken> result1 = res.GetNthResult(j);
                            foreach (var column in columns)
                            {
                                string fieldName;
                                if (fields.TryGetValue(column, out fieldName))
                                {
                                    var val = GetValue(fieldName, result, result1);
                                    csvWriter.WriteField(val);
                                }
                                else
                                {
                                    csvWriter.WriteField("");
                                }
                            }
                            csvWriter.NextRecord();
                            csvWriter.Flush();
                        }
                    }
                    else
                    {
                        foreach (var column in columns)
                        {
                            string fieldName;
                            if (fields.TryGetValue(column, out fieldName))
                            {
                                var val = GetValue(fieldName, result, null);
                                csvWriter.WriteField(val);
                            }
                            else
                            {
                                csvWriter.WriteField("");
                            }
                        }
                        csvWriter.NextRecord();
                        csvWriter.Flush();
                    }
                }
            }
        }
    }
    public class Connection
    {
        public Settings settings;
        public string securityToken;
        private string base_url;
        public Connection(string url, Settings s)
        {
            base_url = url;
            settings = s;
        }
        private void AddSecurityToken(HttpWebRequest httpRequest)
        {
            if(String.IsNullOrEmpty(securityToken))
            {
                string methodVal;
                settings.authentication.TryGetValue("method", out methodVal);
                if (methodVal == "basic")
                {
                    string authorization = settings.authentication["username"] + ":" + settings.authentication["password"];
                    string base64 = Convert.ToBase64String(Encoding.Default.GetBytes(authorization));
                    httpRequest.Headers.Add("Authorization", "Basic " + base64);
                }
            }
        }
        public Response Get(string url, string lookup_url, string filter = "")
        {
            try
            {
                /*
                using (StreamReader r = new StreamReader(@"..\..\..\sample\purchase.json"))
                {
                    string json = r.ReadToEnd();
                    Response res = new Response(json);
                    using (StreamReader r1 = new StreamReader(@"..\..\..\sample\purchaseItem.json"))
                    {
                        json = r1.ReadToEnd();
                        Response res1 = new Response(json);
                        res.SetNthLookupResponse(1, res1);
                    }
                    return res;
                } */
                string connection_url = base_url + url;
                connection_url += "?" + filter;
                if (filter != "")
                    connection_url += "&";
                connection_url += "$format=json";
                HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create(connection_url);
                httpRequest.Method = "GET";
                AddSecurityToken(httpRequest);

                using (HttpWebResponse myres = (HttpWebResponse)httpRequest.GetResponse())
                {
                    StreamReader sr = new StreamReader(myres.GetResponseStream(), Encoding.UTF8);
                    string resstring = sr.ReadToEnd();
                    Response res = new Response(resstring);
                    for (var i = 0; i < res.GetNumResults(); i++)
                    {
                        IDictionary<string, JToken> result = res.GetNthResult(i);
                        JToken value;
                        if (result.TryGetValue(lookup_url, out value))
                        {
                            var deferred = value.Value<JToken>("__deferred");
                            string child_uri = deferred.Value<string>("uri");
                            child_uri += "?$format=json";
                            httpRequest = (HttpWebRequest)WebRequest.Create(child_uri);
                            httpRequest.Method = "GET";
                            AddSecurityToken(httpRequest);
                            using (HttpWebResponse myres1 = (HttpWebResponse)httpRequest.GetResponse())
                            {
                                sr = new StreamReader(myres1.GetResponseStream(), Encoding.UTF8);
                                resstring = sr.ReadToEnd();
                                Response res1 = new Response(resstring);
                                res.SetNthLookupResponse(i, res1);
                            }
                        }
                    }
                    return res;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.StackTrace);
            }
            return null;
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            /* API_PURCHASEORDER_PROCESS_SRV_Entities context =
                 new API_PURCHASEORDER_PROCESS_SRV_Entities(new Uri("https://my300194.s4hana.ondemand.com/sap/opu/odata/sap/API_PURCHASEORDER_PROCESS_SRV/A_PurchaseOrder?%24top=50"));

             context.SendingRequest += new EventHandler<System.Data.Services.Client.SendingRequestEventArgs>(OnSendingRequest);
             var billings = context.A_PurchaseOrder.get();
            */
            string path = "settings.json";
            if (!File.Exists(path))
            {
                path = Path.Combine(@"..\..\..\", path);
            }
            Settings current_settings;
            try
            {
                using (StreamReader r = new StreamReader(path))
                {
                    string json = r.ReadToEnd();
                    current_settings = JsonConvert.DeserializeObject<Settings>(json);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Something is wrong with the settings.json");
                Console.WriteLine(e.StackTrace);
                return;
            }
            try
            {
                Connection current_connection = new Connection(current_settings.url, current_settings);
                foreach( var current_api in current_settings.apis )
                {
                    Response res = current_connection.Get(current_api.api, current_api.lookup_api, current_api.filter);
                    res.Serialize("output.csv", current_settings.default_output, current_api.fields);
                }

                Console.WriteLine("press any key to continue....");
                Console.ReadKey();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.StackTrace);
            }
            finally
            {
            }

            /*
            Console.ReadKey();

            foreach (var billing in billings)
            {
                Console.WriteLine(billing.AccountingDocument + "|" + billing.BillingDocumentDate + "|" + billing.CompanyCode);

                //Console.ReadKey(); 
            }

            Console.WriteLine("loop finished");
            Console.ReadKey();
            */
        }
        /*
        private static void OnSendingRequest(object sender, SendingRequestEventArgs e)
        {
            string authorization = "UserName" + ":" + "PassWord";
            string base64 = Convert.ToBase64String(Encoding.Default.GetBytes(authorization));

            base64 = "Basic " + base64;

            // Add an Authorization header that contains an OAuth WRAP access token to the request.
            e.RequestHeaders.Add("Authorization", base64);
        }*/
    }
}
