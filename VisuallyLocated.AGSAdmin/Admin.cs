﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VisuallyLocated.ArcGIS.Server
{
    public class Admin
    {
        private readonly string _serverUrl;

        public Admin(string serverUrl)
        {
            if (string.IsNullOrEmpty(serverUrl)) throw new ArgumentNullException("serverUrl");

            _serverUrl = serverUrl;
        }

        public Task<UserToken> GenerateTokenAsync(string user, string password)
        {
            var parameters = GetBaseParameters();
            parameters.Add(Constants.UserName, user);
            parameters.Add(Constants.Password, password);
            parameters.Add(Constants.Client, Constants.RequestIP);

            //return RequestResultAsync(parameters, Constants.TokenUrl, true)
            //    .ContinueWith(t => JsonConvert.DeserializeObject<UserToken>(t.Result),
            //    currentThread);
            var taskCompletionSource = new TaskCompletionSource<UserToken>();
            RequestResult(parameters, Constants.TokenUrl,
                result => taskCompletionSource.SetResult(JsonConvert.DeserializeObject<UserToken>(result)),
                true);
            return taskCompletionSource.Task;
        }

        public Task<IEnumerable<Machine>> GetMachinesAsync(UserToken token)
        {
            var currentThread = TaskScheduler.FromCurrentSynchronizationContext();
            var parameters = GetBaseParameters(token);

            return RequestResultAsync(parameters, Constants.TokenUrl)
                .ContinueWith(t => JsonConvert.DeserializeObject<IEnumerable<Machine>>(t.Result),
                              currentThread);
        }

        public Task<Service> GetServiceAsync(UserToken token, string serviceName, ServiceType serviceType, string folder = null)
        {
            var currentThread = TaskScheduler.FromCurrentSynchronizationContext();
            var parameters = GetBaseParameters(token);

            string url = GetServiceTypeUrl(GetFolderUrl(Constants.ServicesUrl, folder), serviceName, serviceType);
            return RequestResultAsync(parameters, url)
                .ContinueWith(t =>
                {
                    var service = JsonConvert.DeserializeObject
                            <Service>(t.Result);
                    return service;
                }, currentThread);
        }

        public Task<ServicesContainer> GetServicesAsync(UserToken token, bool getFulldetails, string folder = null)
        {
            var currentThread = TaskScheduler.FromCurrentSynchronizationContext();
            var parameters = GetBaseParameters(token);
            parameters.Add(Constants.Detail, getFulldetails.ToString(CultureInfo.InvariantCulture));

            return RequestResultAsync(parameters, GetFolderUrl(Constants.ServicesUrl, folder))
                .ContinueWith(t =>
                                  {
                                      var services = JsonConvert.DeserializeObject<ServicesContainer>(t.Result);
                                      return services;
                                  }, currentThread);
        }

        public Task<RequestStatus> CreateFolderAsync(UserToken token, string folderName)
        {
            var currentThread = TaskScheduler.FromCurrentSynchronizationContext();
            var parameters = GetBaseParameters(token);
            parameters.Add(Constants.FolderName, folderName);
            parameters.Add(Constants.Description, "None");

            return RequestResultAsync(parameters, Constants.CreateFolderUrl, true)
                .ContinueWith(t =>
                {
                    var status = JsonConvert.DeserializeObject<RequestStatus>(t.Result);
                    return status;
                }, currentThread);
        }

        public Task<RequestStatus> EditServiceAsync(UserToken token, Service service, string folder = null)
        {
            var parameters = GetBaseParameters(token);
            var taskCompletionSource = new TaskCompletionSource<RequestStatus>();

            string url = GetServiceTypeUrl(GetFolderUrl(Constants.ServicesUrl, folder), service.Name, service.Type);
            RequestResult(parameters, string.Format("{0}{1}", url, Constants.EditUrl), 
                result => taskCompletionSource.SetResult(RequestStatus.Success), true,
                "service=" + JsonConvert.SerializeObject(service));
            return taskCompletionSource.Task;
        }

        public Task<UploadResult> UploadItemAsync(UserToken token, UploadParameters parameters)
        {
            var taskCompletionSource = new TaskCompletionSource<UploadResult>();
            var uploader = new UploadFormDataRequest(token, parameters);
            uploader.UploadAsync(_serverUrl,
                result => taskCompletionSource.SetResult(JsonConvert.DeserializeObject<UploadResult>(result)));
            return taskCompletionSource.Task;
        }

        public Task<RequestStatus> RegisterItemAsync(UserToken token, string id)
        {
            var currentThread = TaskScheduler.FromCurrentSynchronizationContext();
            var parameters = GetBaseParameters(token);
            parameters[Constants.ID] = id;

            return RequestResultAsync(parameters, Constants.RegisterUrl, true)
                          .ContinueWith(t => RequestStatus.Success, currentThread);
        }

        public Task<RequestStatus> StopServicesAsync(UserToken token, string folder = null)
        {
            return StartOrStopServicesAsync(token, Constants.Stop, folder);
        }

        public Task<RequestStatus> StartServicesAsync(UserToken token, string folder = null)
        {
            return StartOrStopServicesAsync(token, Constants.Start, folder);
        }

        public Task<RequestStatus> StartServiceAsync(UserToken token, string serviceName, ServiceType serviceType, string folder = null)
        {
            return StartOrStopServiceAsync(serviceName, serviceType, token, Constants.Start, folder);
        }

        public Task<RequestStatus> StopServiceAsync(UserToken token, string serviceName, ServiceType serviceType, string folder = null)
        {
            return StartOrStopServiceAsync(serviceName, serviceType, token, Constants.Stop, folder);
        }

        private Task<RequestStatus> StartOrStopServicesAsync(UserToken token, string action, string folder = null)
        {
            var taskCompletionSource = new TaskCompletionSource<RequestStatus>();
            GetServicesAsync(token, false, folder)
                .ContinueWith(
                servicesTask =>
                {
                    var services = servicesTask.Result.Services.ToList();
                    int serviceCount = services.Count;
                    foreach (var service in services)
                    {
                        StartOrStopServiceAsync(service.Name, service.Type, token, action, folder)
                            .ContinueWith(t =>
                                              {
                                                  // TODO: Need a thread safe way to do this...
                                                  serviceCount--;
                                                  if (serviceCount == 0)
                                                      taskCompletionSource.SetResult(RequestStatus.Success);
                                              });
                    }
                });
            return taskCompletionSource.Task;
        }

        private Task<RequestStatus> StartOrStopServiceAsync(string serviceName, ServiceType serviceType, UserToken token, string action, string folder)
        {
            var parameters = GetBaseParameters(token);
            var currentThread = TaskScheduler.FromCurrentSynchronizationContext();

            string url = GetServiceTypeUrl(GetFolderUrl(Constants.ServicesUrl, folder), serviceName, serviceType);
            return RequestResultAsync(parameters, string.Format("{0}/{1}/", url, action))
                .ContinueWith(t => RequestStatus.Success, currentThread);
        }

        private void RequestResult(IDictionary<string, string> parameters, string adminEndpoint, Action<string> callback, bool post = false, string postData = null)
        {
            var uri = new Uri(string.Format("{0}{1}?{2}", _serverUrl, adminEndpoint, GetQueryString(parameters)), UriKind.Absolute);

            WebRequest webRequest = WebRequest.Create(uri);
            webRequest.ContentType = "application/x-www-form-urlencoded";
            webRequest.Method = post ? "POST" : "GET";
            //webRequest.Accept = "text/plain";

            webRequest.BeginGetRequestStream(ar =>
            {
                if (postData != null)
                {
                    var encoding = new UTF8Encoding();
                    var bytes = encoding.GetBytes(postData);
                    using (Stream os = webRequest.EndGetRequestStream(ar))
                    {
                        os.Write(bytes, 0, bytes.Length);
                    }
                }
                webRequest.BeginGetResponse(
                    ar2 =>
                    {
                        using (var response = webRequest.EndGetResponse(ar2))
                        using (var reader = new StreamReader(response.GetResponseStream()))
                        {
                            string result = reader.ReadToEnd();
                            if (callback != null) callback(result);
                        }
                    }, null);
            }, null);
        }

        private Task<string> RequestResultAsync(IDictionary<string, string> parameters, string adminEndpoint, bool post = false, string postData = null)
        {
            var currentThread = TaskScheduler.FromCurrentSynchronizationContext();
            var uri = new Uri(string.Format("{0}{1}?{2}", _serverUrl, adminEndpoint, GetQueryString(parameters)), UriKind.Absolute);

            WebRequest webRequest = WebRequest.Create(uri);
            webRequest.ContentType = "application/x-www-form-urlencoded";
            webRequest.Method = post ? "POST" : "GET";
            //webRequest.Accept = "text/plain";

            return webRequest.GetReponseAsync()
                .ContinueWith(t =>
                                  {
                                      using (var reader = new StreamReader(t.Result.GetResponseStream()))
                                      {
                                          return reader.ReadToEnd();
                                      }
                                  }, currentThread);
        }

        private static IDictionary<string, string> GetBaseParameters(UserToken token = null)
        {
            var parameters = new Dictionary<string, string>();
            if (token != null)
            {
                parameters.Add(Constants.Token, token.Token);
            }
            parameters.Add(Constants.Format, Constants.Json);
            return parameters;
        }

        private static string GetFolderUrl(string baseUrl, string folder)
        {
            string slash = string.IsNullOrEmpty(folder) ? "" : "/";
            return string.Format("{0}{1}{2}", baseUrl, folder, slash);
        }

        private static string GetServiceTypeUrl(string baseUrl, string serviceName, ServiceType serviceType)
        {
            return string.Format("{0}{1}.{2}", baseUrl, serviceName, serviceType);
        }

        private static string GetQueryString(IDictionary<string, string> parameters)
        {
            return string.Join("&", parameters.Select(kvp => string.Format("{0}={1}", kvp.Key, kvp.Value)).ToArray());
            //return string.Join("&", parameters.Select(kvp => string.Format("{0}={1}", kvp.Key, HttpUtility.UrlEncode(kvp.Value))).ToArray());
        }
    }
}