(() => {
  'use strict';

  const pluginId = 'feedpeek.video-site';
  const updatesEndpoint = 'https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all';
  const browserUserAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36';

  const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
  const objectOrEmpty = value => isObject(value) ? value : {};
  const arrayOrEmpty = value => Array.isArray(value) ? value : [];

  const text = value => {
    if (typeof value === 'string') return value.trim();
    if (typeof value === 'number' && Number.isFinite(value)) return String(value);
    return '';
  };

  const integer = value => {
    if (typeof value === 'number' && Number.isFinite(value)) return Math.trunc(value);
    if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) return Number.parseInt(value, 10);
    return 0;
  };

  const count = value => integer(isObject(value) ? value.count : value);

  const normalizeUrl = value => {
    const url = text(value);
    if (!url) return null;
    if (url.startsWith('//')) return `https:${url}`;
    if (url.startsWith('/')) return `https://www.bilibili.com${url}`;
    return /^https?:\/\//i.test(url) ? url : null;
  };

  const extractBvid = value => {
    const match = text(value).match(/\/video\/(BV[0-9A-Za-z]+)/i);
    return match ? match[1] : '';
  };

  const extractAid = value => {
    const match = text(value).match(/\/av(\d+)/i);
    return match ? integer(match[1]) : 0;
  };

  const publishedAt = value => {
    const seconds = integer(value);
    if (seconds <= 0) return Host.clock.now();
    const date = new Date(seconds * 1000);
    return Number.isNaN(date.getTime()) ? Host.clock.now() : date.toISOString();
  };

  const mapItem = rawItem => {
    const item = objectOrEmpty(rawItem);
    const modules = objectOrEmpty(item.modules);
    const dynamicModule = objectOrEmpty(modules.module_dynamic);
    const major = objectOrEmpty(dynamicModule.major);

    let video = null;
    let isOpus = false;
    if (isObject(major.archive)) video = major.archive;
    else if (isObject(major.pgc)) video = major.pgc;
    else if (isObject(major.opus)) {
      video = major.opus;
      isOpus = true;
    }
    if (!video) return null;

    let url = normalizeUrl(video.jump_url);
    let bvid = text(video.bvid);
    if (!bvid && url) bvid = extractBvid(url);
    let aid = integer(video.aid);
    if (aid <= 0 && isOpus && url) aid = extractAid(url);

    const itemId = bvid || (aid > 0 ? String(aid) : '');
    const title = text(video.title);
    if (!itemId || !title) return null;

    let imageUrl = normalizeUrl(video.cover);
    if (!imageUrl && isOpus) {
      for (const picture of arrayOrEmpty(video.pics)) {
        imageUrl = normalizeUrl(objectOrEmpty(picture).url);
        if (imageUrl) break;
      }
    }

    if (!url) {
      url = bvid
        ? `https://www.bilibili.com/video/${bvid}`
        : `https://www.bilibili.com/av${aid}`;
    }

    const author = objectOrEmpty(modules.module_author);
    const creatorKey = integer(author.mid) > 0 ? String(integer(author.mid)) : null;
    const authorName = text(author.name);
    if (!authorName) return null;

    const moduleStat = objectOrEmpty(modules.module_stat);
    const videoStat = objectOrEmpty(video.stat);
    const likeCount = count(moduleStat.like) || integer(videoStat.like);
    const commentCount = count(moduleStat.comment) || integer(videoStat.reply);
    const metadata = {};
    if (aid > 0) metadata.aid = String(aid);
    if (bvid) metadata.bvid = bvid;
    metadata.likeCount = String(likeCount);
    metadata.commentCount = String(commentCount);

    return {
      itemId,
      title,
      url,
      authorName,
      publishedAt: publishedAt(author.pub_ts),
      creatorKey,
      authorUrl: creatorKey ? `https://space.bilibili.com/${creatorKey}` : null,
      summary: text(video.desc) || null,
      imageUrl,
      authorImageUrl: normalizeUrl(author.face),
      durationText: text(video.duration_text) || null,
      metadata
    };
  };

  const mapApiError = root => {
    const code = integer(root.code);
    if (code === 0) return;
    const message = text(root.message) || `站点接口返回错误：${code}`;
    if (code === -101) Host.raise('AUTH_EXPIRED', message);
    if (code === -352 || code === -412) Host.raise('SITE_RISK_CONTROL', message, { retryable: true });
    if (code === -509) Host.raise('SITE_RATE_LIMITED', message, { retryable: true });
    Host.raise('SITE_API_ERROR', `${message} (${code})`);
  };

  const loadUpdates = async payload => {
    const context = objectOrEmpty(payload?.context);
    if (context.providerId !== pluginId || context.feedId !== 'updates') {
      Host.raise('REQUEST_CONTEXT_INVALID', '更新请求的插件上下文无效。');
    }

    const cursor = text(payload?.cursor);
    const url = cursor
      ? `${updatesEndpoint}?type=video&timezone_offset=-480&offset=${encodeURIComponent(cursor)}`
      : `${updatesEndpoint}?type=video&timezone_offset=-480`;
    const response = await Host.http.request({
      url,
      method: 'GET',
      headers: {
        'User-Agent': browserUserAgent,
        'Accept': 'application/json, text/plain, */*',
        'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
        'Referer': 'https://www.bilibili.com/',
        'Origin': 'https://www.bilibili.com'
      },
      timeoutMs: 15000,
      maxResponseBytes: 4194304
    });

    if (response.status < 200 || response.status >= 300) {
      const retryable = response.status === 429 || response.status >= 500;
      Host.raise(
        response.status === 401 ? 'AUTH_EXPIRED' : 'SITE_HTTP_ERROR',
        `站点接口 HTTP ${response.status}`,
        { retryable }
      );
    }
    if (response.bodyEncoding !== 'utf8') {
      Host.raise('SITE_RESPONSE_INVALID', '站点接口没有返回 JSON 文本。');
    }

    let root;
    try {
      root = JSON.parse(response.body);
    } catch {
      Host.raise('SITE_RESPONSE_INVALID', '站点接口返回了无效 JSON。');
    }
    if (!isObject(root)) Host.raise('SITE_RESPONSE_INVALID', '站点接口响应结构无效。');
    mapApiError(root);

    const data = objectOrEmpty(root.data);
    const items = [];
    const itemIds = new Set();
    for (const rawItem of arrayOrEmpty(data.items)) {
      const item = mapItem(rawItem);
      if (item === null || itemIds.has(item.itemId)) continue;
      itemIds.add(item.itemId);
      items.push(item);
    }
    const nextCursor = text(data.offset);
    const hasMore = Boolean(data.has_more) && Boolean(nextCursor);
    return {
      items,
      nextCursor: hasMore ? nextCursor : null,
      hasMore
    };
  };

  globalThis.FeedPeekPlugin = Object.freeze({
    apiVersion: 1,
    async handle(request) {
      switch (request.method) {
        case 'plugin.handshake':
          return { providerId: pluginId, apiVersion: 1, capabilities: ['feed.updates'] };
        case 'plugin.getStatus':
          return { state: 'ready', message: null, details: null };
        case 'feed.getUpdates':
          if (text(request.payload?.cursor)) Host.raise('CURSOR_UNEXPECTED', '首次更新请求不能携带游标。');
          return loadUpdates(request.payload);
        case 'feed.getMoreUpdates':
          if (!text(request.payload?.cursor)) Host.raise('CURSOR_REQUIRED', '加载更多更新需要游标。');
          return loadUpdates(request.payload);
        default:
          Host.raise('UNSUPPORTED', `Unsupported method: ${request.method}`, {
            context: { method: request.method }
          });
      }
    }
  });
})();
