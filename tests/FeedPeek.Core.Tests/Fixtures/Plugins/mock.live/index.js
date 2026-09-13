globalThis.LiveParsePlugin = {
  apiVersion: 1,

  async getCategories() {
    return [{ id: '100', title: 'Games', icon: '', biz: '', subList: [] }];
  },

  async getRoomDetail(payload) {
    const roomId = String(payload && payload.roomId ? payload.roomId : '');
    if (roomId === '404') {
      Host.raise('MOCK_LIVE_FAILURE', 'Requested mock failure', { context: { roomId: '404' } });
    }
    const response = await Host.http.request({
      url: 'https://live.example.test/room?id=' + roomId,
      method: 'GET',
      headers: { 'User-Agent': 'mock-agent' },
      timeout: 20
    });
    if (response.status !== 200) {
      Host.raise('UPSTREAM', 'Unexpected status ' + String(response.status), {});
    }
    const room = JSON.parse(response.bodyText || '{}');
    return {
      userName: room.userName,
      roomTitle: room.roomTitle,
      roomCover: room.roomCover,
      userHeadImg: room.userHeadImg,
      liveState: room.liveState || '1',
      userId: room.userId,
      roomId: roomId,
      liveWatchedCount: room.liveWatchedCount
    };
  },

  async getLiveState(payload) {
    return { liveState: '1' };
  },

  async validateCredential() {
    return globalThis.__mockLiveSynced === true
      ? { state: 'valid', message: '' }
      : { state: 'missing', message: 'not synced' };
  },

  async setCredential() {
    return globalThis.__mockLiveLogin.setCookie();
  },

  async clearCredential() {
    return globalThis.__mockLiveLogin.clearCookie();
  }
};
