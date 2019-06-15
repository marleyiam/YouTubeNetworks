﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Google;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Humanizer;
using Polly;
using Serilog;
using SysExtensions;
using SysExtensions.Collections;
using SysExtensions.Fluent.IO;
using SysExtensions.IO;
using SysExtensions.Net;

namespace YtReader {
  public class YtClient {
    public YtClient(AppCfg cfg, ILogger log) {
      Cfg = cfg;
      Log = log;
      YtService = new YouTubeService();
      var keys = cfg.YTApiKeys ?? throw new InvalidOperationException("configuration requires YTApiKeys");
      AvailableKeys = new ConcurrentDictionary<string, string>(keys.Select(k => new KeyValuePair<string, string>(k, null)));
      Start = DateTime.UtcNow;
    }

    public ConcurrentDictionary<string, string>AvailableKeys { get; set; }

    public DateTime Start { get; }

    public AppCfg Cfg { get; }
    ILogger Log { get; }
    YouTubeService YtService { get; }

    async Task<T> GetResponse<T>(YouTubeBaseServiceRequest<T> request) {
      void SetRequestKey() {
        if (AvailableKeys.Count == 0)
          throw new InvalidOperationException("Ran out of quota for all available keys");
        request.Key = AvailableKeys.First().Key;
      }

      SetRequestKey();
      return await Policy
        // handle quote limits
        .Handle<GoogleApiException>(g => {
          if (g.HttpStatusCode != HttpStatusCode.Forbidden) return false;
          AvailableKeys.TryRemove(request.Key, out var value);
          Log.Error(g, "Quota exceeded, no longer using key {Key}", request.Key);
          SetRequestKey();
          return true;
        })
        .RetryForeverAsync()
        // wrap generic transient fault handling
        .WrapAsync(Policy
          .Handle<HttpRequestException>()
          .Or<GoogleApiException>(g => g.HttpStatusCode.IsTransient())
          .WaitAndRetryAsync(3, i => i.ExponentialBackoff(1.Seconds())))
        .ExecuteAsync(request.ExecuteAsync);
    }

    #region Trending

    public async Task SaveTrendingCsv() {
      var trending = await Trending(100);
      trending.AddRange(await Trending(100, UsCategoryEnum.Entertainment));

      var videos = trending
        .Select(i =>
          new {
            Include = 0,
            Id = i.VideoId,
            i.ChannelTitle,
            Title = i.VideoTitle,
            CategoryName = ((UsCategoryEnum) int.Parse(i.CategoryId)).EnumString(),
            i.ChannelId
          });

      TrendingDir.CreateDirectories();
      videos.WriteToCsv(TrendingDir.Combine(DateTime.UtcNow.FileSafeTimestamp() + " Trending.csv"));
    }

    async Task<ICollection<VideoData>> Trending(int max, UsCategoryEnum? category = null) {
      var s = YtService.Videos.List("snippet");
      s.Chart = VideosResource.ListRequest.ChartEnum.MostPopular;
      s.RegionCode = "us";
      s.MaxResults = 50;
      if (category != null)
        s.VideoCategoryId = ((int) category).ToString();

      var videos = new List<VideoData>();
      while (videos.Count < max) {
        var res = await s.ExecuteAsync();
        var trending = res.Items.Where(i => !IsExcluded(i.Snippet.CategoryId));
        videos.AddRange(trending.Take(max - videos.Count).Select(ToVideoData));
        if (res.NextPageToken == null)
          break;
        s.PageToken = res.NextPageToken;
      }

      return videos;
    }

    FPath TrendingDir => "Trending".AsPath().InAppData("YoutubeNetworks");

    enum UsCategoryEnum {
      FilmAnimation = 1,
      AutoVehicles = 2,
      Music = 10,
      PetsAnimals = 15,
      Sports = 17,
      ShortMovies = 18,
      TravelEvents = 19,
      Gaming = 20,
      VideoBlogging = 21,
      PplBlogs = 22,
      Comedy = 23,
      Entertainment = 24,
      NewsPolitics = 25,
      HowToStyle = 26,
      Education = 27,
      ScienceTech = 28,
      NonprofitsActivism = 29,
      Movies = 20,
      Animation = 31,
      ActionAdventure = 23,
      Classics = 33,
      Comedy2 = 34,
      Doco = 35,
      Drama = 36,
      Family = 37,
      Foreign = 38,
      Horror = 39,
      SciFi = 40,
      Thriller = 41,
      Shorts = 42,
      Trailers = 44
    }

    public static readonly HashSet<string> ExcludeCategories = new[] {
      UsCategoryEnum.FilmAnimation, UsCategoryEnum.AutoVehicles, UsCategoryEnum.Music,
      UsCategoryEnum.PetsAnimals, UsCategoryEnum.Sports, UsCategoryEnum.ShortMovies,
      UsCategoryEnum.TravelEvents, UsCategoryEnum.Gaming,
      UsCategoryEnum.HowToStyle, UsCategoryEnum.Movies, UsCategoryEnum.Animation, UsCategoryEnum.ActionAdventure,
      UsCategoryEnum.Classics, UsCategoryEnum.Comedy2, UsCategoryEnum.Drama, UsCategoryEnum.Family,
      UsCategoryEnum.Foreign, UsCategoryEnum.Horror, UsCategoryEnum.SciFi, UsCategoryEnum.Thriller,
      UsCategoryEnum.Shorts, UsCategoryEnum.Trailers
    }.Select(i => ((int) i).ToString()).ToHashSet();

    public static bool IsExcluded(string catId) => ExcludeCategories.Contains(catId);

    #endregion

    #region Videos

    VideoData ToVideoData(Video v) {
      var r = new VideoData {
        VideoId = v.Id,
        VideoTitle = v.Snippet.Title,
        Description = v.Snippet.Description,
        ChannelTitle = v.Snippet.ChannelTitle,
        ChannelId = v.Snippet.ChannelId,
        Language = v.Snippet.DefaultLanguage,
        PublishedAt = v.Snippet.PublishedAt ?? DateTime.MinValue,
        CategoryId = v.Snippet.CategoryId,
        Stats = new VideoStats {
          Views = v.Statistics?.ViewCount,
          Likes = v.Statistics?.LikeCount,
          Dislikes = v.Statistics?.DislikeCount,
          Updated = DateTime.UtcNow
        },
        Updated = DateTime.UtcNow
      };
      if (v.Snippet.Tags != null)
        r.Tags.AddRange(v.Snippet.Tags);
      if (v.TopicDetails?.RelevantTopicIds != null)
        r.Topics.AddRange(v.TopicDetails.RelevantTopicIds);

      return r;
    }

    public async Task<VideoData> VideoData(string id) {
      var s = YtService.Videos.List("snippet,topicDetails,statistics");
      s.Id = id;

      VideoListResponse response;
      try {
        response = await GetResponse(s);
      }
      catch (GoogleApiException ex) {
        Log.Error("Error {ex} VideoData for {VideoId} ", ex, id);
        return null;
      }

      var v = response.Items.FirstOrDefault();
      if (v == null) return null;

      var data = ToVideoData(v);

      return data;
    }

    public async Task<ICollection<RecommendedVideoListItem>> GetRelatedVideos(string id) {
      var s = YtService.Search.List("snippet");
      s.RelatedToVideoId = id;
      s.Type = "video";
      s.MaxResults = Cfg.YtReader.CacheRelated;

      SearchListResponse response;
      try {
        response = await GetResponse(s);
      }
      catch (GoogleApiException ex) {
        Log.Error("Error {ex} GetRelatedVideos for {VideoId} ", ex, id);
        return null;
      }

      var vids = new List<RecommendedVideoListItem>();
      var rank = 1;
      foreach (var item in response.Items) {
        vids.Add(new RecommendedVideoListItem {
          VideoId = item.Id.VideoId,
          VideoTitle = item.Snippet.Title,
          ChannelId = item.Snippet.ChannelId,
          ChannelTitle = item.Snippet.ChannelTitle,
          Rank = rank
        });

        rank++;
      }

      return vids;
    }

    #endregion

    #region Channels

    /// <summary>
    ///   The most popular in that channel. Video's do not include related data.
    /// </summary>
    public async Task<ICollection<ChannelVideoListItem>> VideosInChannel(ChannelData c, DateTime publishedAfter,
      DateTime? publishBefore = null) {
      var s = YtService.Search.List("snippet");
      s.ChannelId = c.Id;
      s.PublishedAfter = publishedAfter;
      s.PublishedBefore = publishBefore;
      s.MaxResults = 50;
      s.Order = SearchResource.ListRequest.OrderEnum.Date;
      s.Type = "video";

      var vids = new List<ChannelVideoListItem>();
      while (true) {
        var res = await GetResponse(s);
        vids.AddRange(res.Items.Where(v => v.Snippet.PublishedAt != null).Select(v => new ChannelVideoListItem {
          VideoId = v.Id.VideoId,
          VideoTitle = v.Snippet.Title,
          PublishedAt = (DateTime) v.Snippet.PublishedAt,
          Updated = DateTime.UtcNow
        }));
        if (res.NextPageToken == null)
          break;
        s.PageToken = res.NextPageToken;
      }

      return vids;
    }

    public async Task<ChannelData> ChannelData(string id) {
      var s = YtService.Channels.List("snippet,statistics");
      s.Id = id;
      var r = await GetResponse(s);
      var c = r.Items.FirstOrDefault();
      if (c == null) return new ChannelData {Id = id, Title = "N/A"};

      var data = new ChannelData {
        Id = id,
        Title = c.Snippet.Title,
        Description = c.Snippet.Description,
        Country = c.Snippet.Country,
        Thumbnails = c.Snippet.Thumbnails,
        Stats = new ChannelStats {
          ViewCount = c.Statistics.ViewCount,
          SubCount = c.Statistics.SubscriberCount,
          Updated = DateTime.UtcNow
        }
      };

      return data;
    }

    #endregion
  }

  public class ChannelData {
    public string Id { get; set; }
    public string Title { get; set; }
    public string Country { get; set; }
    public string Description { get; set; }
    public ThumbnailDetails Thumbnails { get; set; }
    public ChannelStats Stats { get; set; }

    public override string ToString() => Title;
  }

  public class ChannelStats {
    public ulong? ViewCount { get; set; }
    public ulong? SubCount { get; set; }
    public DateTime Updated { get; set; }
  }

  public class VideoData : ChannelVideoListItem {
    public string Description { get; set; }
    public string ChannelTitle { get; set; }
    public string ChannelId { get; set; }
    public string Language { get; set; }

    public string CategoryId { get; set; }

    public ICollection<string> Topics { get; } = new List<string>();
    public ICollection<string> Tags { get; } = new List<string>();

    public VideoStats Stats { get; set; } = new VideoStats();

    public override string ToString() => $"{ChannelTitle} {VideoTitle}";
  }

  public class VideoStats {
    public ulong? Views { get; set; }
    public ulong? Likes { get; set; }
    public ulong? Dislikes { get; set; }
    public DateTime Updated { get; set; }
  }

  public class VideoItem {
    public string VideoId { get; set; }
    public string VideoTitle { get; set; }

    public override string ToString() => VideoTitle;
  }

  public class RecommendedVideoListItem : VideoItem {
    public string ChannelTitle { get; set; }
    public string ChannelId { get; set; }
    public int Rank { get; set; }

    public override string ToString() => $"{Rank}. {ChannelTitle}: {VideoTitle}";
  }

  public class ChannelVideoListItem : VideoItem {
    public DateTime PublishedAt { get; set; }
    public DateTime Updated { get; set; }
  }
}