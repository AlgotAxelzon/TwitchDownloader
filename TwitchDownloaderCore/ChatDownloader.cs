﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchDownloaderCore.Options;
using TwitchDownloaderCore.TwitchObjects;

namespace TwitchDownloaderCore
{
    public class ChatDownloader
    {
        ChatDownloadOptions downloadOptions;
        enum DownloadType { Clip, Video }

        public ChatDownloader(ChatDownloadOptions DownloadOptions)
        {
            downloadOptions = DownloadOptions;
        }

        public async Task DownloadAsync(IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            using (WebClient client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                client.Headers.Add("Accept", "application/vnd.twitchtv.v5+json; charset=UTF-8");
                client.Headers.Add("Client-Id", "kimne78kx3ncx6brgo4mv6wki5h1ko");

                DownloadType downloadType = downloadOptions.Id.All(x => Char.IsDigit(x)) ? DownloadType.Video : DownloadType.Clip;
                string videoId = "";

                List<Comment> comments = new List<Comment>();
                ChatRoot chatRoot = new ChatRoot() { streamer = new Streamer(), video = new VideoTime(), comments = comments };

                double videoStart = 0.0;
                double videoEnd = 0.0;
                double videoDuration = 0.0;
                int errorCount = 0;

                if (downloadType == DownloadType.Video)
                {
                    videoId = downloadOptions.Id;
                    GqlVideoResponse taskInfo = await TwitchHelper.GetVideoInfo(Int32.Parse(videoId));
                    chatRoot.streamer.name = taskInfo.data.video.owner.displayName;
                    chatRoot.streamer.id = int.Parse(taskInfo.data.video.owner.id);
                    videoStart = downloadOptions.CropBeginning ? downloadOptions.CropBeginningTime : 0.0;
                    videoEnd = downloadOptions.CropEnding ? downloadOptions.CropEndingTime : taskInfo.data.video.lengthSeconds;
                }
                else
                {
                    GqlClipResponse taskInfo = await TwitchHelper.GetClipInfo(downloadOptions.Id);
                    videoId = taskInfo.data.clip.video.id;
                    downloadOptions.CropBeginning = true;
                    downloadOptions.CropBeginningTime = taskInfo.data.clip.videoOffsetSeconds;
                    downloadOptions.CropEnding = true;
                    downloadOptions.CropEndingTime = downloadOptions.CropBeginningTime + taskInfo.data.clip.durationSeconds;
                    chatRoot.streamer.name = taskInfo.data.clip.broadcaster.displayName;
                    chatRoot.streamer.id = int.Parse(taskInfo.data.clip.broadcaster.id);
                    videoStart = taskInfo.data.clip.videoOffsetSeconds;
                    videoEnd = taskInfo.data.clip.videoOffsetSeconds + taskInfo.data.clip.durationSeconds;
                }

                chatRoot.video.start = videoStart;
                chatRoot.video.end = videoEnd;
                videoDuration = videoEnd - videoStart;

                double latestMessage = videoStart - 1;
                bool isFirst = true;
                string cursor = "";

                while (latestMessage < videoEnd)
                {
                    string response;

                    try
                    {
                        if (isFirst)
                            response = await client.DownloadStringTaskAsync(String.Format("https://api.twitch.tv/v5/videos/{0}/comments?content_offset_seconds={1}", videoId, videoStart));
                        else
                            response = await client.DownloadStringTaskAsync(String.Format("https://api.twitch.tv/v5/videos/{0}/comments?cursor={1}", videoId, cursor));
                        errorCount = 0;
                    }
                    catch (WebException ex)
                    {
                        await Task.Delay(1000 * errorCount);
                        errorCount++;

                        if (errorCount >= 10)
                            throw ex;

                        continue;
                    }

                    CommentResponse commentResponse = JsonConvert.DeserializeObject<CommentResponse>(response);

                    foreach (var comment in commentResponse.comments)
                    {
                        if (latestMessage < videoEnd && comment.content_offset_seconds > videoStart)
                            comments.Add(comment);

                        latestMessage = comment.content_offset_seconds;
                    }
                    if (commentResponse._next == null)
                        break;
                    else
                        cursor = commentResponse._next;

                    int percent = (int)Math.Floor((latestMessage - videoStart) / videoDuration * 100);
                    progress.Report(new ProgressReport() { reportType = ReportType.Percent, data = percent });
                    progress.Report(new ProgressReport() { reportType = ReportType.MessageInfo, data = $"Downloading {percent}%" });

                    cancellationToken.ThrowIfCancellationRequested();

                    if (isFirst)
                        isFirst = false;

                }

                if (downloadOptions.EmbedEmotes && downloadOptions.IsJson)
                {
                    progress.Report(new ProgressReport() { reportType = ReportType.Message, data = "Downloading + Embedding Emotes" });
                    chatRoot.emotes = new Emotes();
                    List<FirstPartyEmoteData> firstParty = new List<FirstPartyEmoteData>();
                    List<ThirdPartyEmoteData> thirdParty = new List<ThirdPartyEmoteData>();

                    string cacheFolder = Path.Combine(Path.GetTempPath(), "TwitchDownloader", "cache");
                    List<TwitchEmote> thirdPartyEmotes = new List<TwitchEmote>();
                    List<TwitchEmote> firstPartyEmotes = new List<TwitchEmote>();

                    await Task.Run(() => {
                        thirdPartyEmotes = TwitchHelper.GetThirdPartyEmotes(chatRoot.streamer.id, cacheFolder);
                        firstPartyEmotes = TwitchHelper.GetEmotes(comments, cacheFolder).ToList();
                    });

                    foreach (TwitchEmote emote in thirdPartyEmotes)
                    {
                        ThirdPartyEmoteData newEmote = new ThirdPartyEmoteData();
                        newEmote.id = emote.id;
                        newEmote.imageScale = emote.imageScale;
                        newEmote.data = emote.imageData;
                        newEmote.name = emote.name;
                        thirdParty.Add(newEmote);
                    }
                    foreach (TwitchEmote emote in firstPartyEmotes)
                    {
                        FirstPartyEmoteData newEmote = new FirstPartyEmoteData();
                        newEmote.id = emote.id;
                        newEmote.imageScale = 1;
                        newEmote.data = emote.imageData;
                        firstParty.Add(newEmote);
                    }

                    chatRoot.emotes.thirdParty = thirdParty;
                    chatRoot.emotes.firstParty = firstParty;
                }

                if (downloadOptions.IsJson)
                {
                    using (TextWriter writer = File.CreateText(downloadOptions.Filename))
                    {
                        var serializer = new JsonSerializer();
                        serializer.Serialize(writer, chatRoot);
                    }
                }
                else
                {
                    using (StreamWriter sw = new StreamWriter(downloadOptions.Filename))
                    {
                        foreach (var comment in chatRoot.comments)
                        {
                            string username = comment.commenter.display_name;
                            string message = comment.message.body;
                            if (downloadOptions.TimeFormat == TimestampFormat.Utc)
                            {
                                string timestamp = comment.created_at.ToString("u").Replace("Z", " UTC");
                                sw.WriteLine(String.Format("[{0}] {1}: {2}", timestamp, username, message));
                            }
                            else if (downloadOptions.TimeFormat == TimestampFormat.Relative)
                            {
                                TimeSpan time = new TimeSpan(0, 0, (int)comment.content_offset_seconds);
                                string timestamp = time.ToString(@"h\:mm\:ss");
                                sw.WriteLine(String.Format("[{0}] {1}: {2}", timestamp, username, message));
                            }
                            else if (downloadOptions.TimeFormat == TimestampFormat.None)
                            {
                                sw.WriteLine(String.Format("{0}: {1}", username, message));
                            }
                        }

                        sw.Flush();
                        sw.Close();
                    }
                }
                
                chatRoot = null;
                GC.Collect();
            }
        }
    }
}