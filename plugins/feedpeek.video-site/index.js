(() => {
  'use strict';

  const pluginId = 'feedpeek.video-site';
  const updatesEndpoint = 'https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/all';
  const historyEndpoint = 'https://api.bilibili.com/x/web-interface/history/cursor';
  const viewLaterEndpoint = 'https://api.bilibili.com/x/v2/history/toview';
  const accountProfileEndpoint = 'https://api.bilibili.com/x/web-interface/nav';
  const addViewLaterEndpoint = 'https://api.bilibili.com/x/v2/history/toview/add';
  const removeViewLaterEndpoint = 'https://api.bilibili.com/x/v2/history/toview/del';
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

  const formatDuration = value => {
    const seconds = Math.max(0, integer(value));
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const remainder = seconds % 60;
    return hours > 0
      ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`
      : `${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`;
  };

  const formatProgress = (progress, duration) => {
    const total = Math.max(0, integer(duration));
    const current = Math.max(0, integer(progress));
    if (total <= 0) return null;
    if (current <= 0) return `未观看 · 共 ${formatDuration(total)}`;
    return `已观看 ${formatDuration(Math.min(current, total))} / ${formatDuration(total)}`;
  };

  const requestJson = async options => {
    const response = await Host.http.request({
      ...options,
      headers: {
        'User-Agent': browserUserAgent,
        'Accept': 'application/json, text/plain, */*',
        'Accept-Language': 'zh-CN,zh;q=0.9,en;q=0.8',
        'Referer': 'https://www.bilibili.com/',
        'Origin': 'https://www.bilibili.com',
        ...(options.headers || {})
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
    return root;
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
    if (code === -111) Host.raise('CSRF_INVALID', message);
    if (code === -352 || code === -412) Host.raise('SITE_RISK_CONTROL', message, { retryable: true });
    if (code === -509) Host.raise('SITE_RATE_LIMITED', message, { retryable: true });
    Host.raise('SITE_API_ERROR', `${message} (${code})`);
  };

  const mapHistoryItem = rawItem => {
    const item = objectOrEmpty(rawItem);
    const history = objectOrEmpty(item.history);
    if (text(history.business) && text(history.business).toLowerCase() !== 'archive') return null;
    const bvid = text(history.bvid);
    const aid = integer(history.oid) || integer(item.aid);
    const itemId = bvid || (aid > 0 ? String(aid) : '');
    const title = text(item.title);
    if (!itemId || !title) return null;
    const creatorKey = integer(item.author_mid) > 0 ? String(integer(item.author_mid)) : null;
    const url = normalizeUrl(item.uri)
      || (bvid ? `https://www.bilibili.com/video/${bvid}` : `https://www.bilibili.com/av${aid}`);
    const duration = integer(item.duration);
    const page = integer(history.page);
    const videos = integer(item.videos);
    const metadata = { aid: String(aid) };
    if (bvid) metadata.bvid = bvid;
    if (page > 0) metadata.page = String(page);
    if (videos > 0) metadata.videoCount = String(videos);
    return {
      itemId,
      title,
      url,
      authorName: text(item.author_name) || '未知作者',
      publishedAt: publishedAt(item.view_at),
      creatorKey,
      authorUrl: creatorKey ? `https://space.bilibili.com/${creatorKey}` : null,
      summary: formatProgress(item.progress, duration),
      imageUrl: normalizeUrl(item.cover),
      authorImageUrl: normalizeUrl(item.author_face),
      durationText: duration > 0 ? formatDuration(duration) : null,
      metadata
    };
  };

  const mapViewLaterItem = rawItem => {
    const item = objectOrEmpty(rawItem);
    const owner = objectOrEmpty(item.owner);
    const bvid = text(item.bvid);
    const aid = integer(item.aid);
    const itemId = bvid || (aid > 0 ? String(aid) : '');
    const title = text(item.title);
    if (!itemId || !title) return null;
    const creatorKey = integer(owner.mid) > 0 ? String(integer(owner.mid)) : null;
    const duration = integer(item.duration);
    const metadata = { aid: String(aid) };
    if (bvid) metadata.bvid = bvid;
    return {
      itemId,
      title,
      url: normalizeUrl(item.redirect_url)
        || (bvid ? `https://www.bilibili.com/video/${bvid}` : `https://www.bilibili.com/av${aid}`),
      authorName: text(owner.name) || '未知作者',
      publishedAt: publishedAt(item.add_at),
      creatorKey,
      authorUrl: creatorKey ? `https://space.bilibili.com/${creatorKey}` : null,
      summary: formatProgress(item.progress, duration),
      imageUrl: normalizeUrl(item.pic),
      authorImageUrl: normalizeUrl(owner.face),
      durationText: duration > 0 ? formatDuration(duration) : null,
      metadata
    };
  };

  const uniqueMappedItems = (rawItems, mapper) => {
    const items = [];
    const itemIds = new Set();
    for (const rawItem of arrayOrEmpty(rawItems)) {
      const item = mapper(rawItem);
      if (item === null || itemIds.has(item.itemId)) continue;
      itemIds.add(item.itemId);
      items.push(item);
    }
    return items;
  };

  const requireContext = (payload, feedId) => {
    const context = objectOrEmpty(payload?.context);
    if (context.providerId !== pluginId || context.feedId !== feedId) {
      Host.raise('REQUEST_CONTEXT_INVALID', '请求的插件上下文无效。');
    }
    return context;
  };

  const loadHistory = async payload => {
    requireContext(payload, 'history');
    const cursor = text(payload?.cursor);
    const parts = cursor ? cursor.split(':') : [];
    const max = parts.length === 2 ? integer(parts[0]) : 0;
    const viewAt = parts.length === 2 ? integer(parts[1]) : 0;
    if (cursor && (max <= 0 || viewAt < 0)) Host.raise('CURSOR_INVALID', '历史记录游标无效。');
    const pageSize = Math.max(1, Math.min(30, integer(payload?.pageSize) || 20));
    const root = await requestJson({
      url: `${historyEndpoint}?max=${max}&view_at=${viewAt}&business=archive&ps=${pageSize}`,
      method: 'GET'
    });
    const data = objectOrEmpty(root.data);
    const items = uniqueMappedItems(data.list, mapHistoryItem);
    const next = objectOrEmpty(data.cursor);
    const nextMax = integer(next.max);
    const nextViewAt = integer(next.view_at);
    const hasMore = (Boolean(data.has_more) || Boolean(next.has_more)
      || (items.length > 0 && nextMax > 0 && nextMax !== max)) && nextMax > 0;
    return {
      items,
      nextCursor: hasMore ? `${nextMax}:${nextViewAt}` : null,
      hasMore
    };
  };

  const loadViewLater = async payload => {
    requireContext(payload, 'viewLater');
    const cursor = text(payload?.cursor);
    const parts = cursor ? cursor.split(':') : [];
    const pageNumber = cursor ? integer(parts[0]) : 1;
    const loadedCount = cursor && parts.length === 2 ? integer(parts[1]) : 0;
    if (pageNumber <= 0 || loadedCount < 0) Host.raise('CURSOR_INVALID', '稍后再看游标无效。');
    const pageSize = Math.max(1, Math.min(30, integer(payload?.pageSize) || 20));
    const root = await requestJson({
      url: `${viewLaterEndpoint}?pn=${pageNumber}&ps=${pageSize}`,
      method: 'GET'
    });
    const data = objectOrEmpty(root.data);
    const rawItems = arrayOrEmpty(data.list);
    const items = uniqueMappedItems(rawItems, mapViewLaterItem);
    const nextLoadedCount = loadedCount + rawItems.length;
    const totalCount = Math.max(0, integer(data.count));
    const hasMore = rawItems.length > 0 && nextLoadedCount < totalCount;
    return {
      items,
      nextCursor: hasMore ? `${pageNumber + 1}:${nextLoadedCount}` : null,
      hasMore
    };
  };

  const getItemActions = payload => {
    const context = objectOrEmpty(payload?.context);
    if (context.providerId !== pluginId) Host.raise('REQUEST_CONTEXT_INVALID', '条目操作上下文无效。');
    const metadata = objectOrEmpty(payload?.metadata);
    const aid = integer(metadata.aid);
    if (aid <= 0) return [];
    const common = { aid: String(aid) };
    return [
      {
        actionId: 'viewLater.add',
        localizationKey: null,
        fallbackText: '添加到稍后再看',
        iconSemantic: 'save',
        dangerLevel: 'none',
        requiresConfirmation: false,
        isRepeatable: false,
        payload: common
      },
      {
        actionId: 'viewLater.remove',
        localizationKey: null,
        fallbackText: '从稍后再看移除',
        iconSemantic: 'remove',
        dangerLevel: 'none',
        requiresConfirmation: false,
        isRepeatable: false,
        payload: common
      }
    ];
  };

  const executeItemAction = async payload => {
    const context = objectOrEmpty(payload?.context);
    if (context.providerId !== pluginId || !text(context.credentialProfileId)) {
      Host.raise('REQUEST_CONTEXT_INVALID', '条目操作上下文无效。');
    }
    const actionId = text(payload?.actionId);
    if (actionId !== 'viewLater.add' && actionId !== 'viewLater.remove') {
      Host.raise('ACTION_UNSUPPORTED', '不支持的条目操作。');
    }
    const aid = integer(objectOrEmpty(payload?.payload).aid);
    if (aid <= 0) Host.raise('ACTION_PAYLOAD_INVALID', '当前条目缺少 avid。');
    const secret = await Host.credentials.getOpaqueSecret({
      credentialProfileId: context.credentialProfileId,
      name: 'bili_jct'
    });
    const csrf = text(secret?.value);
    if (!csrf) Host.raise('CSRF_MISSING', '登录状态中缺少 bili_jct。');
    await requestJson({
      url: actionId === 'viewLater.add' ? addViewLaterEndpoint : removeViewLaterEndpoint,
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: `aid=${encodeURIComponent(String(aid))}&csrf=${encodeURIComponent(csrf)}`,
      bodyEncoding: 'utf8'
    });
    return { completed: 'true', isSaved: actionId === 'viewLater.add' ? 'true' : 'false' };
  };

  const getAccountProfile = async payload => {
    const context = objectOrEmpty(payload?.context || payload);
    if (context.providerId !== pluginId || !text(context.credentialProfileId)) {
      Host.raise('REQUEST_CONTEXT_INVALID', '账户资料上下文无效。');
    }
    const root = await requestJson({
      url: accountProfileEndpoint,
      method: 'GET'
    });
    const data = objectOrEmpty(root.data);
    if (!Boolean(data.isLogin)) Host.raise('AUTH_EXPIRED', '登录状态已失效。');
    const accountId = text(data.mid);
    const displayName = text(data.uname);
    if (!accountId || !displayName) Host.raise('SITE_RESPONSE_INVALID', '站点没有返回有效账户资料。');
    return {
      accountId,
      displayName,
      avatarUrl: normalizeUrl(data.face),
      profileUrl: `https://space.bilibili.com/${accountId}`,
      metadata: null
    };
  };

  const validateAuthentication = async payload => {
    const context = objectOrEmpty(payload?.context || payload);
    const profile = await getAccountProfile(context);
    return { state: 'authenticated', profile, message: null };
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
    const items = uniqueMappedItems(data.items, mapItem);
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
          return {
            providerId: pluginId,
            apiVersion: 1,
            capabilities: ['feed.updates', 'account.auth', 'account.profile', 'account.history', 'collection.viewLater', 'item.actions']
          };
        case 'plugin.getStatus':
          return { state: 'ready', message: null, details: null };
        case 'auth.begin':
          return { state: 'webviewRequired', data: null };
        case 'auth.complete':
          return validateAuthentication(request.payload);
        case 'auth.validate':
          return validateAuthentication(request.payload);
        case 'auth.clear':
          return { state: 'signedOut', profile: null, message: null };
        case 'feed.getUpdates':
          if (text(request.payload?.cursor)) Host.raise('CURSOR_UNEXPECTED', '首次更新请求不能携带游标。');
          return loadUpdates(request.payload);
        case 'feed.getMoreUpdates':
          if (!text(request.payload?.cursor)) Host.raise('CURSOR_REQUIRED', '加载更多更新需要游标。');
          return loadUpdates(request.payload);
        case 'account.getHistory':
          if (text(request.payload?.cursor)) Host.raise('CURSOR_UNEXPECTED', '首次历史记录请求不能携带游标。');
          return loadHistory(request.payload);
        case 'account.getMoreHistory':
          if (!text(request.payload?.cursor)) Host.raise('CURSOR_REQUIRED', '加载更多历史记录需要游标。');
          return loadHistory(request.payload);
        case 'account.getProfile':
          return getAccountProfile(request.payload);
        case 'collection.getViewLater':
          if (text(request.payload?.cursor)) Host.raise('CURSOR_UNEXPECTED', '首次稍后再看请求不能携带游标。');
          return loadViewLater(request.payload);
        case 'collection.getMoreViewLater':
          if (!text(request.payload?.cursor)) Host.raise('CURSOR_REQUIRED', '加载更多稍后再看需要游标。');
          return loadViewLater(request.payload);
        case 'item.getActions':
          return getItemActions(request.payload);
        case 'item.executeAction':
          return executeItemAction(request.payload);
        default:
          Host.raise('UNSUPPORTED', `Unsupported method: ${request.method}`, {
            context: { method: request.method }
          });
      }
    }
  });
})();
