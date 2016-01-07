﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace StackExchange.Opserver.Data.HAProxy
{
    public class HAProxyAdmin
    {
        public const string AllServersKey = "*";
        private static readonly ParallelOptions ParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 3 };

        public static async Task<bool> PerformProxyActionAsync(IEnumerable<Proxy> proxies, string serverName, Action action)
        {
            var result = true;
            var matchingServers = proxies.SelectMany(p => p.Servers.Where(s => s.Name == serverName || serverName.IsNullOrEmpty()).Select(s => new { Proxy = p, Server = s })).ToList();
            Parallel.ForEach(matchingServers, ParallelOptions, pair =>
            {
                // HAProxy will not drain a downed server, do the next best thing: MAINT
                if (action == Action.drain && pair.Server.ProxyServerStatus == ProxyServerStatus.Down)
                    action = Action.maint;

                result = result && PostAction(pair.Proxy, pair.Server, action);
            });
            var polls = matchingServers.Select(p => p.Proxy.Instance).Distinct().Select(p => p.PollAsync(true));
            await Task.WhenAll(polls);
            return result;
        }

        public static async Task<bool> PerformProxyActionAsync(Proxy proxy, Server server, Action action)
        {
            if (server != null)
            {
                var result = true;
                // TODO: Async
                Parallel.ForEach(proxy.Servers, ParallelOptions, s =>
                {
                    result = result && PostAction(proxy, s, action);
                });
                await proxy.Instance.PollAsync(true);
                return result;
            }
            else
            {
                var result = PostAction(proxy, null, action);
                await proxy.Instance.PollAsync(true);
                return result;
            }
        }

        public static async Task<bool> PerformServerActionAsync(string server, Action action)
        {
            var proxies = HAProxyGroup.GetAllProxies();
            var matchingServers = proxies.SelectMany(p => p.Servers.Where(s => s.Name == server).Select(s => new { Proxy = p, Server = s })).ToList();

            var result = true;
            // TODO: Async
            Parallel.ForEach(matchingServers, ParallelOptions, pair =>
                {
                    result = result && PostAction(pair.Proxy, pair.Server, action);
                });
            var polls = matchingServers.Select(p => p.Proxy.Instance).Distinct().Select(p => p.PollAsync(true));
            await Task.WhenAll(polls);
            return result;
        }

        public static async Task<bool> PerformGroupActionAsync(string group, Action action)
        {
            var haGroup = HAProxyGroup.GetGroup(group);
            if (haGroup == null) return false;
            var proxies = haGroup.GetProxies();
            var matchingServers = proxies.SelectMany(p => p.Servers.Select(s => new { Proxy = p, Server = s })).ToList();

            var result = true;
            // TODO: Async
            Parallel.ForEach(matchingServers, ParallelOptions, pair =>
            {
                result = result && PostAction(pair.Proxy, pair.Server, action);
            });
            var polls = matchingServers.Select(p => p.Proxy.Instance).Distinct().Select(p => p.PollAsync(true));
            await Task.WhenAll(polls);
            return result;
        }

        // TODO: Async
        private static bool PostAction(Proxy p, Server server, Action action)
        {
            var instance = p.Instance;
            // if we can't issue any commands, bomb out
            if (instance.AdminUser.IsNullOrEmpty() || instance.AdminPassword.IsNullOrEmpty()) return false;

            var loginInfo = $"{instance.AdminUser}:{instance.AdminPassword}";
            var haproxyUri = new Uri(instance.Url);
            var requestBody = $"s={server.Name}&action={action.ToString().ToLower()}&b={p.Name}";
            var requestHeader = $"POST {haproxyUri.AbsolutePath} HTTP/1.1\r\nHost: {haproxyUri.Host}\r\nContent-Length: {Encoding.GetEncoding("ISO-8859-1").GetBytes(requestBody).Length}\r\nAuthorization: Basic {Convert.ToBase64String(Encoding.Default.GetBytes(loginInfo))}\r\n\r\n";

            try
            {
                var socket = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(haproxyUri.Host, haproxyUri.Port);
                socket.Send(Encoding.UTF8.GetBytes(requestHeader + requestBody));

                var responseBytes = new byte[socket.ReceiveBufferSize];
                socket.Receive(responseBytes);

                var response = Encoding.UTF8.GetString(responseBytes);
                return response.StartsWith("HTTP/1.0 303") || response.StartsWith("HTTP/1.1 303");
            }
            catch (Exception e)
            {
                Current.LogException(e);
                return false;
            }
        }

        public enum Action
        {
            // ReSharper disable InconsistentNaming
            [Description("Set State to READY")]
            ready,
            [Description("Set State to DRAIN")]
            drain,
            [Description("Set State to MAINT")]
            maint,
            [Description("Health: disable checks")]
            dhlth,
            [Description("Health: enable checks")]
            ehlth,
            [Description("Health: force UP")]
            hrunn,
            [Description("Health: force NOLB")]
            hnolb,
            [Description("Health: force DOWN")]
            hdown,
            [Description("Agent: disable checks")]
            dagent,
            [Description("Agent: enable checks")]
            eagent,
            [Description("Agent: force UP")]
            arunn,
            [Description("Agent: force DOWN")]
            adown,
            [Description("Kill Sessions")]
            shutdown,

            Enable,
            Disable
            // ReSharper restore InconsistentNaming
        }
    }
}