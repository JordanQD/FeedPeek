globalThis.FeedPeekPlugin = {
  apiVersion: 1,

  async handle(request) {
    switch (request.method) {
      case "plugin.handshake":
        return {
          providerId: "mock.feed",
          apiVersion: 1,
          preloaded: globalThis.mockPreloaded === true
        };

      case "plugin.getStatus":
        if (request.payload?.mode === "throw") {
          Host.raise("MOCK_FAILURE", "Requested mock failure", {
            context: { mode: "throw" }
          });
        }
        if (request.payload?.mode === "spin") {
          while (true) {}
        }
        if (request.payload?.mode === "allocate") {
          const values = [];
          for (let index = 0; index < 2_000_000; index++) {
            values.push("memory-" + index);
          }
          return { count: values.length };
        }
        if (request.payload?.mode === "fireAndForget") {
          Host.http.request({ url: "https://api.example.test/hang", method: "GET" });
          return { ready: true };
        }
        return { ready: true, now: Host.clock.now(), digest: Host.crypto.md5("FeedPeek") };

      case "feed.getUpdates":
      case "feed.getMoreUpdates": {
        const response = await Host.http.request({
          url: "https://api.example.test/feed",
          method: "GET",
          timeoutMs: 1000,
          maxResponseBytes: 65536
        });
        const data = JSON.parse(response.body);
        const offset = Number(request.payload?.cursor ?? 0);
        const pageSize = Number(request.payload?.pageSize ?? 2);
        const items = data.items.slice(offset, offset + pageSize);
        const nextOffset = offset + items.length;
        return {
          items,
          nextCursor: nextOffset < data.items.length ? String(nextOffset) : null,
          hasMore: nextOffset < data.items.length
        };
      }

      case "item.getActions":
        return [{
          actionId: "mock.save",
          localizationKey: null,
          fallbackText: "Save",
          iconSemantic: "save",
          dangerLevel: "none",
          requiresConfirmation: false,
          isRepeatable: false,
          payload: { itemId: request.payload.itemId }
        }];

      case "item.executeAction": {
        const profileId = request.payload.context.credentialProfileId;
        await Host.credentials.setOpaqueSecret({
          credentialProfileId: profileId,
          name: "lastAction",
          value: request.payload.actionId
        });
        const saved = await Host.credentials.getOpaqueSecret({
          credentialProfileId: profileId,
          name: "lastAction"
        });
        return { completed: true, savedAction: saved.value };
      }

      default:
        Host.raise("UNSUPPORTED", "Unsupported method", {
          context: { method: request.method }
        });
    }
  }
};
