// Copyright (C) 2026, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Duplicati.Library.Interface;
using Duplicati.Server.Database;
using Duplicati.Server.Serialization.Interface;

namespace Duplicati.Library.RestAPI;

public class WebhookNotificationConfig
{
    public string Service { get; set; } = "none";
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Level { get; set; } = "all"; // all, warning_error, error_only
    public string CustomTitle { get; set; } = "";
}

public static class WebhookNotificationSender
{
    private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string LOGTAG = Logging.Log.LogTagFromType(typeof(WebhookNotificationSender));

    public static WebhookNotificationConfig GetConfig(Connection databaseConnection)
    {
        var settings = databaseConnection.GetSettings(Connection.SERVER_SETTINGS_ID)
            .ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase);

        return new WebhookNotificationConfig
        {
            Service = settings.GetValueOrDefault("notification-service") ?? "none",
            Url = settings.GetValueOrDefault("notification-url") ?? "",
            Secret = settings.GetValueOrDefault("notification-secret") ?? "",
            Level = settings.GetValueOrDefault("notification-level") ?? "all",
            CustomTitle = settings.GetValueOrDefault("notification-title") ?? ""
        };
    }

    public static void DispatchTaskCompletion(Connection databaseConnection, IBackup backup, IBasicResults result)
    {
        try
        {
            var config = GetConfig(databaseConnection);
            if (string.IsNullOrWhiteSpace(config.Service) || config.Service.Equals("none", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(config.Url))
                return;

            var isWarning = result.ParsedResult == ParsedResultType.Warning || result.Warnings.Any();
            var isError = result.ParsedResult == ParsedResultType.Error || result.ParsedResult == ParsedResultType.Fatal || result.Errors.Any();

            if (config.Level.Equals("error_only", StringComparison.OrdinalIgnoreCase) && !isError)
                return;

            if (config.Level.Equals("warning_error", StringComparison.OrdinalIgnoreCase) && !isWarning && !isError)
                return;

            Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await SendResultNotificationAsync(config, backup, result, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteWarningMessage(LOGTAG, "SendNotificationError", ex, "Failed to send webhook notification: {0}", ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            Logging.Log.WriteWarningMessage(LOGTAG, "DispatchNotificationError", ex, "Failed to dispatch notification: {0}", ex.Message);
        }
    }

    public static async Task<(bool Success, string Message)> SendTestNotificationAsync(WebhookNotificationConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Service) || config.Service.Equals("none", StringComparison.OrdinalIgnoreCase))
            return (false, "请先选择通知方式（如飞书机器人、企业微信机器人等）");

        if (string.IsNullOrWhiteSpace(config.Url))
            return (false, "请填写 Webhook 地址或 Key");

        var title = string.IsNullOrWhiteSpace(config.CustomTitle) ? "Duplicati 测试通知" : $"{config.CustomTitle} [测试]";
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var content = $"🎉 恭喜！Duplicati 通知机器人配置成功！\n测试时间：{now}\n通知渠道：{GetChannelDisplayName(config.Service)}";

        return await SendPayloadAsync(config, title, content, "Success", null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SendResultNotificationAsync(WebhookNotificationConfig config, IBackup backup, IBasicResults result, CancellationToken cancellationToken)
    {
        var status = result.ParsedResult switch
        {
            ParsedResultType.Success => "成功",
            ParsedResultType.Warning => "存在警告",
            _ => "失败"
        };

        var opType = backup.OperationType == Duplicati.Server.Serialization.OperationType.Sync ? "同步 (Sync)" : "备份 (Backup)";
        var baseTitle = string.IsNullOrWhiteSpace(config.CustomTitle) ? "Duplicati 任务报告" : config.CustomTitle;
        var title = $"{baseTitle}: {backup.Name} [{status}]";

        var sb = new StringBuilder();
        sb.AppendLine($"**任务名称**：{backup.Name}");
        sb.AppendLine($"**任务类型**：{opType}");
        sb.AppendLine($"**执行结果**：{status}");
        sb.AppendLine($"**运行耗时**：{FormatDuration(result.Duration)}");

        if (result is ISyncResults sr)
        {
            sb.AppendLine($"**传输统计**：已上传 {sr.FilesUploaded} 个文件 ({Duplicati.Library.Utility.Utility.FormatSizeString(sr.SizeOfUploadedFiles)})");
            if (sr.FilesDeleted > 0)
                sb.AppendLine($"**删除文件**：{sr.FilesDeleted} 个");
        }
        else if (result is IBackupResults br)
        {
            var uploaded = br.BackendStatistics != null ? Duplicati.Library.Utility.Utility.FormatSizeString(br.BackendStatistics.BytesUploaded) : "0 B";
            var total = Duplicati.Library.Utility.Utility.FormatSizeString(br.SizeOfExaminedFiles);
            sb.AppendLine($"**传输统计**：已上传 {uploaded}，源数据大小 {total}");
        }

        sb.AppendLine($"**完成时间**：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var warnings = result.Warnings.Take(3).ToList();
        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**警告详情**：");
            foreach (var w in warnings)
                sb.AppendLine($"> ⚠️ {FormatLogMessage(w)}");
        }

        var errors = result.Errors.Take(3).ToList();
        if (errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**错误详情**：");
            foreach (var e in errors)
                sb.AppendLine($"> ❌ {FormatLogMessage(e)}");
        }

        await SendPayloadAsync(config, title, sb.ToString(), result.ParsedResult.ToString(), result, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> SendPayloadAsync(WebhookNotificationConfig config, string title, string markdownContent, string status, IBasicResults? result, CancellationToken ct)
    {
        var service = config.Service.ToLowerInvariant();
        try
        {
            switch (service)
            {
                case "wecom":
                case "qywx":
                    return await SendWeComAsync(config, title, markdownContent, status, ct).ConfigureAwait(false);
                case "feishu":
                case "lark":
                    return await SendFeishuAsync(config, title, markdownContent, status, ct).ConfigureAwait(false);
                case "dingtalk":
                    return await SendDingTalkAsync(config, title, markdownContent, status, ct).ConfigureAwait(false);
                case "telegram":
                    return await SendTelegramAsync(config, title, markdownContent, ct).ConfigureAwait(false);
                case "bark":
                    return await SendBarkAsync(config, title, markdownContent, ct).ConfigureAwait(false);
                case "serverchan":
                case "sct":
                    return await SendServerChanAsync(config, title, markdownContent, ct).ConfigureAwait(false);
                case "pushdeer":
                    return await SendPushDeerAsync(config, title, markdownContent, ct).ConfigureAwait(false);
                case "webhook":
                default:
                    return await SendCustomWebhookAsync(config, title, markdownContent, status, result, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return (false, $"发送请求失败: {ex.Message}");
        }
    }

    private static async Task<(bool Success, string Message)> SendWeComAsync(WebhookNotificationConfig config, string title, string markdownContent, string status, CancellationToken ct)
    {
        var url = config.Url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = $"https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key={url}";

        var color = status.Equals("Success", StringComparison.OrdinalIgnoreCase) ? "#00b050" : (status.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "#ff9900" : "#d9534f");
        var formatted = $"### <font color=\"{color}\">{title}</font>\n{markdownContent}";

        var payload = new
        {
            msgtype = "markdown",
            markdown = new { content = formatted }
        };

        return await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> SendFeishuAsync(WebhookNotificationConfig config, string title, string markdownContent, string status, CancellationToken ct)
    {
        var url = config.Url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = $"https://open.feishu.cn/open-apis/bot/v2/hook/{url}";

        var colorTemplate = status.Equals("Success", StringComparison.OrdinalIgnoreCase) ? "green" : (status.Equals("Warning", StringComparison.OrdinalIgnoreCase) ? "orange" : "red");

        var payloadDict = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(config.Secret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var stringToSign = timestamp + "\n" + config.Secret;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(stringToSign));
            var sign = Convert.ToBase64String(hmac.ComputeHash(Array.Empty<byte>()));
            payloadDict["timestamp"] = timestamp;
            payloadDict["sign"] = sign;
        }

        payloadDict["msg_type"] = "interactive";
        payloadDict["card"] = new
        {
            header = new
            {
                title = new { tag = "plain_text", content = title },
                template = colorTemplate
            },
            elements = new object[]
            {
                new
                {
                    tag = "div",
                    text = new { tag = "lark_md", content = markdownContent }
                }
            }
        };

        return await PostJsonAsync(url, payloadDict, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> SendDingTalkAsync(WebhookNotificationConfig config, string title, string markdownContent, string status, CancellationToken ct)
    {
        var url = config.Url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = $"https://oapi.dingtalk.com/robot/send?access_token={url}";

        if (!string.IsNullOrWhiteSpace(config.Secret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var stringToSign = timestamp + "\n" + config.Secret;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.Secret));
            var sign = Uri.EscapeDataString(Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign))));
            url += $"{(url.Contains("?") ? "&" : "?")}timestamp={timestamp}&sign={sign}";
        }

        var payload = new
        {
            msgtype = "markdown",
            markdown = new
            {
                title = title,
                text = $"### {title}\n{markdownContent}"
            }
        };

        return await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> SendTelegramAsync(WebhookNotificationConfig config, string title, string markdownContent, CancellationToken ct)
    {
        var token = config.Secret.Trim();
        var chatId = config.Url.Trim();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            return (false, "Telegram 需要在 Webhook 地址栏填写 Chat ID，在密钥栏填写 Bot Token");

        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        var payload = new
        {
            chat_id = chatId,
            text = $"*{title}*\n\n{markdownContent.Replace("### ", "")}",
            parse_mode = "Markdown"
        };

        return await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> SendBarkAsync(WebhookNotificationConfig config, string title, string markdownContent, CancellationToken ct)
    {
        var url = config.Url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = $"https://api.day.app/{url}";

        var cleanText = markdownContent.Replace("**", "").Replace("### ", "");
        var payload = new
        {
            title = title,
            body = cleanText,
            group = "Duplicati",
            icon = "https://duplicati.com/favicon.ico"
        };

        return await PostJsonAsync($"{url}/", payload, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> SendServerChanAsync(WebhookNotificationConfig config, string title, string markdownContent, CancellationToken ct)
    {
        var key = config.Url.Trim();
        var url = key.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? key : $"https://sctapi.ftqq.com/{key}.send";

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["title"] = title,
            ["desp"] = markdownContent
        });

        using var resp = await HttpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? (true, "通知发送成功") : (false, $"接口返回错误: {body}");
    }

    private static async Task<(bool Success, string Message)> SendPushDeerAsync(WebhookNotificationConfig config, string title, string markdownContent, CancellationToken ct)
    {
        var key = config.Url.Trim();
        var url = "https://api2.pushdeer.com/message/push";

        var payload = new
        {
            pushkey = key,
            text = title,
            desp = markdownContent,
            type = "markdown"
        };

        return await PostJsonAsync(url, payload, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> SendCustomWebhookAsync(WebhookNotificationConfig config, string title, string markdownContent, string status, IBasicResults? result, CancellationToken ct)
    {
        var payload = new
        {
            title = title,
            status = status,
            content = markdownContent,
            duration = result?.Duration.ToString(),
            timestamp = DateTime.Now.ToString("O")
        };

        return await PostJsonAsync(config.Url.Trim(), payload, ct).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Message)> PostJsonAsync(string url, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await HttpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.IsSuccessStatusCode)
        {
            // Parse response for potential business errors (e.g. Feishu code!=0, WeCom errcode!=0)
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("errcode", out var errcode) && errcode.GetInt32() != 0)
                {
                    var errmsg = doc.RootElement.TryGetProperty("errmsg", out var msg) ? msg.GetString() : body;
                    return (false, $"企业微信返回错误: {errmsg}");
                }
                if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
                {
                    var msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : body;
                    return (false, $"飞书返回错误: {msg}");
                }
            }
            catch
            {
            }

            return (true, "通知发送成功！");
        }

        return (false, $"请求失败 ({resp.StatusCode}): {body}");
    }

    private static string GetChannelDisplayName(string service) => service.ToLowerInvariant() switch
    {
        "wecom" or "qywx" => "企业微信机器人",
        "feishu" or "lark" => "飞书机器人",
        "dingtalk" => "钉钉机器人",
        "telegram" => "Telegram 机器人",
        "bark" => "Bark",
        "serverchan" or "sct" => "Server酱",
        "pushdeer" => "PushDeer",
        _ => "自定义 Webhook"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}小时 {duration.Minutes}分 {duration.Seconds}秒";
        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes}分 {duration.Seconds}秒";
        return $"{Math.Max(1, (int)duration.TotalSeconds)}秒";
    }

    private static string FormatLogMessage(string msg)
    {
        if (msg.Length > 200)
            return msg.Substring(0, 200) + "...";
        return msg;
    }
}
