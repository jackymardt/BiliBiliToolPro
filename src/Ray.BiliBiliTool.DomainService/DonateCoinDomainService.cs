﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.Relation;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace Ray.BiliBiliTool.DomainService;

/// <summary>
/// 投币
/// </summary>
public class DonateCoinDomainService(
    ILogger<DonateCoinDomainService> logger,
    IOptionsMonitor<DailyTaskOptions> dailyTaskOptions,
    IAccountApi accountApi,
    ICoinDomainService coinDomainService,
    IVideoDomainService videoDomainService,
    IRelationApi relationApi,
    IVideoApi videoApi
) : IDonateCoinDomainService
{
    private readonly DailyTaskOptions _dailyTaskOptions = dailyTaskOptions.CurrentValue;
    private readonly Dictionary<string, int> _expDic = Config.Constants.ExpDic;
    private readonly Dictionary<string, string> _donateContinueStatusDic = Config
        .Constants
        .DonateCoinCanContinueStatusDic;

    /// <summary>
    /// up的视频稿件总数缓存
    /// </summary>
    private readonly Dictionary<long, int> _upVideoCountDicCatch = new();

    /// <summary>
    /// 已对视频投币数量缓存
    /// </summary>
    private readonly Dictionary<string, int> _alreadyDonatedCoinCountCatch = new();

    /// <summary>
    /// 完成投币任务
    /// </summary>
    public async Task AddCoinsForVideos(BiliCookie ck)
    {
        int needCoins = await GetNeedDonateCoinNum(ck);
        int protectedCoins = _dailyTaskOptions.NumberOfProtectedCoins;
        if (needCoins <= 0)
            return;

        //投币前硬币余额
        decimal coinBalance = await coinDomainService.GetCoinBalance(ck);
        logger.LogInformation("【投币前余额】 : {coinBalance}", coinBalance);
        _ = int.TryParse(
            decimal.Truncate(coinBalance - protectedCoins).ToString(),
            out int unprotectedCoins
        );

        if (coinBalance <= 0)
        {
            logger.LogInformation("因硬币余额不足，今日暂不执行投币任务");
            return;
        }

        if (coinBalance <= protectedCoins)
        {
            logger.LogInformation("因硬币余额达到或低于保留值，今日暂不执行投币任务");
            return;
        }

        //余额小于目标投币数，按余额投
        if (coinBalance < needCoins)
        {
            _ = int.TryParse(decimal.Truncate(coinBalance).ToString(), out needCoins);
            logger.LogInformation("因硬币余额不足，目标投币数调整为: {needCoins}", needCoins);
        }

        //投币后余额小于等于保护值，按保护值允许投
        if (coinBalance - needCoins <= protectedCoins)
        {
            //排除需投等于保护后可投数量相等时的情况
            if (unprotectedCoins != needCoins)
            {
                needCoins = unprotectedCoins;
                logger.LogInformation(
                    "因硬币余额投币后将达到或低于保留值，目标投币数调整为: {needCoins}",
                    needCoins
                );
            }
        }

        int success = 0;
        int tryCount = 10;
        for (int i = 1; i <= tryCount && success < needCoins; i++)
        {
            logger.LogDebug("开始尝试第{num}次", i);

            var video = await TryGetCanDonatedVideo(ck);
            if (video == null)
                continue;

            logger.LogInformation("【视频】{title}", video.Title);

            bool re = await DoAddCoinForVideo(video, _dailyTaskOptions.SelectLike, ck);
            if (re)
                success++;
        }

        if (success == needCoins)
            logger.LogInformation("视频投币任务完成");
        else
            logger.LogInformation("投币尝试超过10次，已终止");

        logger.LogInformation(
            "【硬币余额】{coin}",
            (await accountApi.GetCoinBalanceAsync(ck.ToString())).Data?.Money ?? 0
        );
    }

    /// <summary>
    /// 尝试获取一个可以投币的视频
    /// </summary>
    /// <returns></returns>
    public async Task<UpVideoInfo?> TryGetCanDonatedVideo(BiliCookie ck)
    {
        UpVideoInfo? result;

        //从配置的up中随机尝试获取1次
        result = await TryGetCanDonateVideoByConfigUps(1, ck);
        if (result != null)
            return result;

        //然后从特别关注列表尝试获取1次
        result = await TryGetCanDonateVideoBySpecialUps(1, ck);
        if (result != null)
            return result;

        //然后从普通关注列表获取1次
        result = await TryGetCanDonateVideoByFollowingUps(1, ck);
        if (result != null)
            return result;

        //最后从排行榜尝试5次
        result = await TryGetCanDonateVideoByRegion(5, ck);

        return result;
    }

    /// <summary>
    /// 为视频投币
    /// </summary>
    /// <param name="aid">av号</param>
    /// <param name="multiply">投币数量</param>
    /// <param name="select_like">是否同时点赞 1是0否</param>
    /// <returns>是否投币成功</returns>
    public async Task<bool> DoAddCoinForVideo(UpVideoInfo video, bool select_like, BiliCookie ck)
    {
        BiliApiResponse result;
        try
        {
            var request = new AddCoinRequest(video.Aid, ck.BiliJct)
            {
                Select_like = select_like ? 1 : 0,
            };
            var referer =
                $"https://www.bilibili.com/video/{video.Bvid}/?spm_id_from=333.1007.tianma.1-1-1.click&vd_source=80c1601a7003934e7a90709c18dfcffd";
            result = await videoApi.AddCoinForVideo(request, ck.ToString(), referer);
        }
        catch (Exception)
        {
            return false;
        }

        if (result.Code == 0)
        {
            _expDic.TryGetValue("每日投币", out int exp);
            logger.LogInformation("投币成功，经验+{exp} √", exp);
            return true;
        }

        if (_donateContinueStatusDic.Any(x => x.Key == result.Code.ToString()))
        {
            logger.LogError("投币失败，原因：{msg}", result.Message);
            return false;
        }
        else
        {
            string errorMsg = $"投币发生未预计异常：{result.Message}";
            logger.LogError(errorMsg);
            throw new Exception(errorMsg);
        }
    }

    #region private

    /// <summary>
    /// 获取今日的目标投币数
    /// </summary>
    /// <returns></returns>
    private async Task<int> GetNeedDonateCoinNum(BiliCookie ck)
    {
        //获取自定义配置投币数
        int configCoins = _dailyTaskOptions.NumberOfCoins;

        if (configCoins <= 0)
        {
            logger.LogInformation("已配置为跳过投币任务");
            return configCoins;
        }

        //已投的硬币
        int alreadyCoins = await coinDomainService.GetDonatedCoins(ck);
        //目标
        //int targetCoins = configCoins > Constants.MaxNumberOfDonateCoins
        //    ? Constants.MaxNumberOfDonateCoins
        //    : configCoins;
        int targetCoins = configCoins;

        logger.LogInformation("【今日已投】{already}枚", alreadyCoins);
        logger.LogInformation("【目标欲投】{already}枚", targetCoins);

        if (targetCoins > alreadyCoins)
        {
            int needCoins = targetCoins - alreadyCoins;
            logger.LogInformation("【还需再投】{need}枚", needCoins);
            return needCoins;
        }

        logger.LogInformation("已完成投币任务，不需要再投啦~");
        return 0;
    }

    /// <summary>
    /// 尝试从配置的up主里随机获取一个可以投币的视频
    /// </summary>
    /// <param name="tryCount"></param>
    /// <returns></returns>
    private async Task<UpVideoInfo?> TryGetCanDonateVideoByConfigUps(int tryCount, BiliCookie ck)
    {
        //是否配置了up主
        if (_dailyTaskOptions.SupportUpIdList.Count == 0)
            return null;

        return await TryCanDonateVideoByUps(_dailyTaskOptions.SupportUpIdList, tryCount, ck);
        ;
    }

    /// <summary>
    /// 尝试从特别关注的Up主中随机获取一个可以投币的视频
    /// </summary>
    /// <param name="tryCount"></param>
    /// <returns></returns>
    private async Task<UpVideoInfo?> TryGetCanDonateVideoBySpecialUps(int tryCount, BiliCookie ck)
    {
        //获取特别关注列表
        var request = new GetSpecialFollowingsRequest(long.Parse(ck.UserId));
        BiliApiResponse<List<UpInfo>> specials = await relationApi.GetFollowingsByTag(
            request,
            ck.ToString()
        );
        if (specials.Data == null || specials.Data.Count == 0)
            return null;

        return await TryCanDonateVideoByUps(
            specials.Data.Select(x => x.Mid).ToList(),
            tryCount,
            ck
        );
    }

    /// <summary>
    /// 尝试从普通关注的Up主中随机获取一个可以投币的视频
    /// </summary>
    /// <param name="tryCount"></param>
    /// <returns></returns>
    private async Task<UpVideoInfo?> TryGetCanDonateVideoByFollowingUps(int tryCount, BiliCookie ck)
    {
        //获取特别关注列表
        var request = new GetFollowingsRequest(long.Parse(ck.UserId));
        BiliApiResponse<GetFollowingsResponse> result = await relationApi.GetFollowings(
            request,
            ck.ToString()
        );
        if (result.Data.Total == 0)
            return null;

        return await TryCanDonateVideoByUps(
            result.Data.List.Select(x => x.Mid).ToList(),
            tryCount,
            ck
        );
    }

    /// <summary>
    /// 尝试从排行榜中获取一个没有看过的视频
    /// </summary>
    /// <param name="tryCount"></param>
    /// <returns></returns>
    private async Task<UpVideoInfo?> TryGetCanDonateVideoByRegion(int tryCount, BiliCookie ck)
    {
        try
        {
            for (int i = 0; i < tryCount; i++)
            {
                RankingInfo video = await videoDomainService.GetRandomVideoOfRanking();
                if (!await IsCanDonate(video.Aid.ToString(), ck))
                    continue;
                return new UpVideoInfo()
                {
                    Aid = video.Aid,
                    Bvid = video.Bvid,
                    Title = video.Title,
                    Length = "00:15",
                };
            }
        }
        catch (Exception e)
        {
            //ignore
            logger.LogWarning("异常：{msg}", e);
        }
        return null;
    }

    /// <summary>
    /// 尝试从指定的up主集合中随机获取一个可以尝试投币的视频
    /// </summary>
    /// <param name="upIds"></param>
    /// <param name="tryCount"></param>
    /// <returns></returns>
    private async Task<UpVideoInfo?> TryCanDonateVideoByUps(
        List<long> upIds,
        int tryCount,
        BiliCookie ck
    )
    {
        if (upIds.Count == 0)
            return null;

        try
        {
            //尝试tryCount次
            for (int i = 1; i <= tryCount; i++)
            {
                //获取随机Up主Id
                long randomUpId = upIds[new Random().Next(0, upIds.Count)];

                if (randomUpId == 0 || randomUpId == long.MinValue)
                    continue;

                if (randomUpId.ToString() == ck.UserId)
                {
                    logger.LogDebug("不能为自己投币");
                    continue;
                }

                //该up的视频总数
                if (!_upVideoCountDicCatch.TryGetValue(randomUpId, out int videoCount))
                {
                    videoCount = await videoDomainService.GetVideoCountOfUp(randomUpId, ck);
                    _upVideoCountDicCatch.Add(randomUpId, videoCount);
                }
                if (videoCount == 0)
                    continue;

                var videoInfo = await videoDomainService.GetRandomVideoOfUp(
                    randomUpId,
                    videoCount,
                    ck
                );
                logger.LogDebug("获取到视频{aid}({title})", videoInfo?.Aid, videoInfo?.Title);

                //检查是否可以投
                if (videoInfo == null || !await IsCanDonate(videoInfo.Aid.ToString(), ck))
                    continue;

                return videoInfo;
            }
        }
        catch (Exception e)
        {
            //ignore
            logger.LogWarning("异常：{msg}", e);
        }

        return null;
    }

    /// <summary>
    /// 已为视频投币个数是否小于最大限制
    /// </summary>
    /// <param name="aid">av号</param>
    /// <returns></returns>
    private async Task<bool> IsDonatedLessThenLimitCoinsForVideo(string aid, BiliCookie ck)
    {
        try
        {
            //获取已投币数量
            if (!_alreadyDonatedCoinCountCatch.TryGetValue(aid, out int multiply))
            {
                multiply = (
                    await videoApi.GetDonatedCoinsForVideo(
                        new GetAlreadyDonatedCoinsRequest(long.Parse(aid)),
                        ck.ToString()
                    )
                )
                    .Data
                    .Multiply;
                _alreadyDonatedCoinCountCatch.TryAdd(aid, multiply);
            }

            logger.LogDebug("已为Av{aid}投过{num}枚硬币", aid, multiply);

            if (multiply >= 2)
                return false;

            //获取该视频可投币数量
            int limitCoinNum =
                (await videoDomainService.GetVideoDetail(aid)).Copyright == 1
                    ? 2 //原创，最多可投2枚
                    : 1; //转载，最多可投1枚
            logger.LogDebug("该视频的最大投币数为{num}", limitCoinNum);

            return multiply < limitCoinNum;
        }
        catch (Exception e)
        {
            //ignore
            logger.LogWarning("异常：{mag}", e);
            return false;
        }
    }

    /// <summary>
    /// 检查获取到的视频是否可以投币
    /// </summary>
    /// <param name="aid"></param>
    /// <returns></returns>
    private async Task<bool> IsCanDonate(string aid, BiliCookie ck)
    {
        //本次运行已经尝试投过的,不进行重复投（不管成功还是失败，凡取过尝试过的，不重复尝试）
        if (_alreadyDonatedCoinCountCatch.Any(x => x.Key == aid))
        {
            logger.LogDebug("重复视频，丢弃处理");
            return false;
        }

        //已经投满2个币的，不能再投
        if (!await IsDonatedLessThenLimitCoinsForVideo(aid, ck))
        {
            logger.LogDebug("超出单个视频投币数量限制，丢弃处理");
            return false;
        }

        return true;
    }

    #endregion
}
