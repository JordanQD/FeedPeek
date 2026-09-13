globalThis.__mockLiveLogin = {
  async setCookie() {
    globalThis.__mockLiveSynced = true;
    return { ok: true };
  },
  async clearCookie() {
    globalThis.__mockLiveSynced = false;
    return { ok: true };
  }
};
