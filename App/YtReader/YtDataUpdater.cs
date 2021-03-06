﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Common;
using Mutuo.Etl;
using Serilog;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Text;
using SysExtensions.Threading;
using YtReader.Yt;
using YtReader.YtWebsite;
using VideoItem = YtReader.YtWebsite.VideoItem;

namespace YtReader {
  public enum UpdateType {
    All,
    Channels,

    // go back and populate recommendations for videos that are missing any recorded recs
    AllWithMissingRecs
  }
  
  public static class RefreshHelper {
    public static bool IsOlderThan(this DateTime updated, TimeSpan age, DateTime? now = null) => (now ?? DateTime.UtcNow) - updated > age;
    public static bool IsYoungerThan(this DateTime updated, TimeSpan age, DateTime? now = null) => !updated.IsOlderThan(age, now);
  }

  public class YtDataUpdater {
    readonly UpdateType               UpdateType;
    readonly Func<Task<DbConnection>> GetConnection;
    YtStore                           Store { get; }
    AppCfg                            Cfg   { get; }
    ILogger                           Log   { get; }
    readonly YtScraper                Scraper;
    readonly YtClient                 Api;

    public YtDataUpdater(YtStore store, AppCfg cfg, UpdateType updateType, Func<Task<DbConnection>> getConnection, ILogger log) {
      UpdateType = updateType;
      GetConnection = getConnection;
      Store = store;
      Cfg = cfg;
      Log = log;
      Scraper = new YtScraper(cfg.Scraper);
      Api = new YtClient(cfg.YTApiKeys, log);
    }

    YtReaderCfg RCfg => Cfg.YtReader;
    //bool Expired(DateTime updated, TimeSpan refreshAge) => (RCfg.To ?? DateTime.UtcNow) - updated > refreshAge;

    public async Task UpdateData() {
      Log.Information("Starting data update: {Type}", UpdateType);
      var sw = Stopwatch.StartNew();

      var channels = await UpdateChannels().WithDuration();
      Log.Information("Updated stats for {Channels} channels in {Duration}", channels.Result.Count, channels.Duration);

      if (UpdateType == UpdateType.Channels) {
        Log.Information("Update complete");
      }
      else {
        using var conn = await GetConnection();
        var channelResults = await channels.Result
          .Where(c => c.Status == ChannelStatus.Alive)
          .Select((c, i) => (c, i)).BlockTransform(async item => {
            var (c, i) = item;
            var log = Log
              .ForContext("ChannelId", c.ChannelId)
              .ForContext("Channel", c.ChannelTitle);
            try {
              await UpdateAllInChannel(c, conn, log);
              log.Information("{Channel} - Completed {Count}/{Total} update of videos/recs/captions in {Duration}",
                c.ChannelTitle, i, channels.Result.Count, sw.Elapsed);
              return (c, Success: true);
            }
            catch (Exception ex) {
              Log.Error(ex, "Error updating channel {Channel}: {Error}", c.ChannelTitle, ex.Message);
              return (c, Success: false);
            }
          }, Cfg.ParallelChannels);

        var requestStats = Scraper.RequestStats;
        Log.Information("Update complete {ChannelsComplete} channel videos/captions/recs, {ChannelsFailed} failed in {Duration}, {DirectRequests} direct requests, {ProxyRequests} proxy requests",
          channelResults.Count(c => c.Success), channelResults.Count(c => !c.Success), sw.Elapsed, requestStats.direct, requestStats.proxy);
      }
    }

    async Task<IReadOnlyCollection<ChannelStored2>> UpdateChannels() {
      var store = Store.ChannelStore;

      Log.Information("Starting channels update. Limited to ({Included})",
        Cfg.LimitedToSeedChannels?.HasItems() == true ? Cfg.LimitedToSeedChannels.Join("|") : "All");

      async Task<ChannelStored2> UpdateChannel(ChannelSheet channel) {
        var log = Log.ForContext("Channel", channel.Title).ForContext("ChannelId", channel.Id);

        var channelData = new ChannelData {Id = channel.Id, Title = channel.Title};
        try {
          channelData = await Api.ChannelData(channel.Id) ?? // Use API to get channel instead of scraper. We get better info faster
                        new ChannelData {Id = channel.Id, Title = channel.Title, Status = ChannelStatus.Dead};
          log.Information("{Channel} - read channel details", channelData.Title);
        }
        catch (Exception ex) {
          channelData.Status = ChannelStatus.Dead;
          log.Error(ex, "{Channel} - Error when updating details for channel : {Error}", channel.Title, ex.Message);
        }
        var channelStored = new ChannelStored2 {
          ChannelId = channel.Id,
          ChannelTitle = channelData.Title ?? channel.Title,
          Status = channelData.Status,
          MainChannelId = channel.MainChannelId,
          Description = channelData.Description,
          LogoUrl = channelData.Thumbnails?.Default__?.Url,
          Subs = channelData.Stats?.SubCount,
          ChannelViews = channelData.Stats?.ViewCount,
          Country = channelData.Country,
          Updated = DateTime.UtcNow,
          Relevance = channel.Relevance,
          LR = channel.LR,
          HardTags = channel.HardTags,
          SoftTags = channel.SoftTags,
          UserChannels = channel.UserChannels
        };
        return channelStored;
      }

      var seeds = await ChannelSheets.Channels(Cfg.Sheets, Log);

      var channels = await seeds.Where(c => Cfg.LimitedToSeedChannels.IsEmpty() || Cfg.LimitedToSeedChannels.Contains(c.Id))
        .BlockTransform(UpdateChannel, Cfg.DefaultParallel,
          progressUpdate: p => Log.Debug("Reading channels {ChannelCount}/{ChannelTotal}", p.CompletedTotal, seeds.Count));

      if (channels.Any())
        await store.Append(channels);

      return channels;
    }

    async Task UpdateAllInChannel(ChannelStored2 c, DbConnection conn, ILogger log) {
      if (c.StatusMessage.HasValue()) {
        log.Information("{Channel} - Not updating videos/recs/captions because it has a status msg: {StatusMessage} ",
          c.ChannelTitle, c.StatusMessage);
        return;
      }
      log.Information("{Channel} - Starting channel update of videos/recs/captions", c.ChannelTitle);

      // fix updated if missing. Remove once all records have been updated
      var vidStore = Store.VideoStore(c.ChannelId);

      var md = await vidStore.LatestFileMetadata();
      var lastUpload = md?.Ts?.ParseFileSafeTimestamp();
      var lastModified = md?.Modified;

      var recentlyUpdated = lastModified != null && lastModified.Value.IsYoungerThan(RCfg.RefreshAllAfter);

      // get the oldest date for videos to store updated statistics for. This overlaps so that we have a history of video stats.
      var uploadedFrom = md == null ? RCfg.From : DateTime.UtcNow - RCfg.RefreshVideosWithin;

      if (recentlyUpdated)
        log.Information("{Channel} - skipping update, video stats have been updated recently {LastModified}", c.ChannelTitle, lastModified);

      var vids = recentlyUpdated ? null : await ChannelVidItems(c, uploadedFrom, log).ToListAsync();
      if (vids != null) {
        await SaveVids(c, vids, vidStore, lastUpload, log);
        await SaveNewCaptions(c, vids, log);
      }
      if (vids != null || UpdateType == UpdateType.AllWithMissingRecs)
        await SaveRecs(c, vids, conn, log);
    }

    static async Task SaveVids(ChannelStored2 c, IReadOnlyCollection<VideoItem> vids, AppendCollectionStore<VideoStored2> vidStore, DateTime? uploadedFrom,
      ILogger log) {
      var updated = DateTime.UtcNow;
      var vidsStored = vids.Select(v => new VideoStored2 {
        VideoId = v.Id,
        Title = v.Title,
        Description = v.Description,
        Duration = v.Duration,
        Keywords = v.Keywords.ToList(),
        Statistics = v.Statistics,
        Thumbnails = v.Thumbnails,
        ChannelId = c.ChannelId,
        ChannelTitle = c.ChannelTitle,
        UploadDate = v.UploadDate.UtcDateTime,
        Updated = updated
      }).ToList();

      if (vidsStored.Count > 0)
        await vidStore.Append(vidsStored);

      var newVideos = vidsStored.Count(v => uploadedFrom == null || v.UploadDate > uploadedFrom);

      log.Information("{Channel} - Recorded {VideoCount} videos. {NewCount} new, {UpdatedCount} updated",
        c.ChannelTitle, vids.Count, newVideos, vids.Count - newVideos);
    }

    async IAsyncEnumerable<VideoItem> ChannelVidItems(ChannelStored2 c, DateTime uploadFrom, ILogger log) {
      await foreach (var vids in Scraper.GetChannelUploadsAsync(c.ChannelId, log))
      foreach (var v in vids)
        if (v.UploadDate > uploadFrom) yield return v;
        else yield break; // break on the first video older than updateFrom.
    }

    /// <summary>
    ///   Saves captions for all new videos from the vids list
    /// </summary>
    async Task SaveNewCaptions(ChannelStored2 channel, IEnumerable<VideoItem> vids, ILogger log) {
      var store = Store.CaptionStore(channel.ChannelId);
      var lastUpload = (await store.LatestFileMetadata())?.Ts.ParseFileSafeTimestamp(); // last video upload we have captions for

      async Task<VideoCaptionStored2> GetCaption(VideoItem v) {
        var videoLog = log.ForContext("VideoId", v.Id);

        ClosedCaptionTrack track;
        try {
          var captions = await Scraper.GetCaptions(v.Id, log);
          var enInfo = captions.FirstOrDefault(t => t.Language.Code == "en");
          if (enInfo == null) return null;
          track = await Scraper.GetClosedCaptionTrackAsync(enInfo, videoLog);
        }
        catch (Exception ex) {
          log.Warning(ex, "Unable to get captions for {VideoID}: {Error}", v.Id, ex.Message);
          return null;
        }

        return new VideoCaptionStored2 {
          VideoId = v.Id,
          UploadDate = v.UploadDate.UtcDateTime,
          Updated = DateTime.Now,
          Info = track.Info,
          Captions = track.Captions
        };
      }

      var captionsToStore =
        (await vids.Where(v => lastUpload == null || v.UploadDate.UtcDateTime > lastUpload)
          .BlockTransform(GetCaption, Cfg.DefaultParallel)).NotNull().ToList();

      if (captionsToStore.Any())
        await store.Append(captionsToStore);

      log.Information("{Channel} - Saved {Captions} captions", channel.ChannelTitle, captionsToStore.Count);
    }

    /// <summary>
    ///   Saves recs for all of the given vids
    /// </summary>
    async Task SaveRecs(ChannelStored2 c, IReadOnlyCollection<VideoItem> vids, DbConnection conn, ILogger log) {
      var recStore = Store.RecStore(c.ChannelId);

      var toUpdate = UpdateType == UpdateType.AllWithMissingRecs
        ? await VideosWithNoRecs(c, conn)
        : await VideoToUpdateRecs(vids, recStore);

      var recs = await toUpdate.BlockTransform(
        async v => (fromId:v.Id, fromTitle:v.Title, recs: await Scraper.GetRecs(v.Id, log)),
        Cfg.DefaultParallel);

      // read failed recs from the API (either because of an error, or because the video is 18+ restricted)
      var failed = recs.Where(v => v.recs.None()).ToList();

      if (failed.Any()) {
        var apiRecs = await failed.BlockTransform(async f => {
          ICollection<RecommendedVideoListItem> related = new List<RecommendedVideoListItem>();
          try {
            related = await Api.GetRelatedVideos(f.fromId);
          }
          catch (Exception ex) {
            log.Warning(ex, "Unable to get related videos for {VideoId}: {Error}", f.fromId, ex.Message);
          }
          return (f.fromId, f.fromTitle, recs: related.NotNull().Select(r => new Rec {
            Source = RecSource.Api,
            ToChannelTitle = r.ChannelTitle,
            ToChannelId = r.ChannelId,
            ToVideoId = r.VideoId,
            ToVideoTitle = r.VideoTitle,
            Rank = r.Rank
          }).ToReadOnly());
        });

        recs = recs.Concat(apiRecs).ToList();
        
        log.Information("{Channel} - {Videos} videos recommendations fell back to using the API: {VideoList}",
          c.ChannelTitle, failed.Count, apiRecs.Select(r => r.fromId));
      }

      var updated = DateTime.UtcNow;
      var recsStored = recs
        .SelectMany(v => v.recs.Select((r, i) => new RecStored2 {
          FromChannelId = c.ChannelId,
          FromVideoId = v.fromId,
          FromVideoTitle = v.fromTitle,
          ToChannelTitle = r.ToChannelTitle,
          ToChannelId = r.ToChannelId,
          ToVideoId = r.ToVideoId,
          ToVideoTitle = r.ToVideoTitle,
          Rank = i + 1,
          Source = r.Source,
          Updated = updated
        })).ToList();

      if (recsStored.Any())
        await recStore.Append(recsStored);

      Log.Information("{Channel} - Recorded {RecCount} recs: {Recs}", c.ChannelTitle, recsStored.Count, recs.Select(v => new {Id=v.fromId, v.recs.Count}).ToList());
    }

    async Task<IReadOnlyCollection<(string Id, string Title)>> VideosWithNoRecs(ChannelStored2 c, DbConnection connection) {
      var cmd = connection.CreateCommand();
      cmd.CommandText = $@"select v.video_id, v.video_title
      from video_latest v
        where
      v.channel_id = '{c.ChannelId}'
      and not exists(select * from rec r where r.from_video_id = v.video_id)
                     group by v.video_id, v.video_title";
      var reader = await cmd.ExecuteReaderAsync();
      var ids = new List<(string, string)>();
      while (await reader.ReadAsync()) ids.Add((reader["VIDEO_ID"].ToString(), reader["VIDEO_TITLE"].ToString()));

      Log.Information("{Channel} - found {Recommendations} video's missing recommendations", c.ChannelTitle, ids.Count);
      return ids;
    }

    async Task<IReadOnlyCollection<(string Id, string Title)>> VideoToUpdateRecs(IEnumerable<VideoItem> vids, AppendCollectionStore<RecStored2> recStore) {
      var prevUpdateMeta = await recStore.LatestFileMetadata();
      var prevUpdate = prevUpdateMeta?.Ts.ParseFileSafeTimestamp();
      var vidsDesc = vids.OrderByDescending(v => v.UploadDate).ToList();
     
      var toUpdate = prevUpdate == null ? 
        vidsDesc : //  all videos if this is the first time for this channel
        // new vids since the last rec update, or the vid was created in the last RefreshRecsWithin (e.g. 30d)
        vidsDesc.Where(v =>v.UploadDate > prevUpdate || v.UploadDate.UtcDateTime.IsYoungerThan(RCfg.RefreshRecsWithin)).ToList();  
      var deficit = RCfg.RefreshRecsMin - toUpdate.Count;
      if (deficit > 0)
        toUpdate.AddRange(vidsDesc.Where(v => toUpdate.All(u => u.Id != v.Id))
          .Take(deficit)); // if we don't have new videos, refresh the min amount by adding videos 
      return toUpdate.Select(v => (v.Id, v.Title)).ToList();
    }
  }
}