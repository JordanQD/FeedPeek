using System.Threading;
using System.Threading.Tasks;

namespace FeedPeek.Services;

internal interface IMainPanelPage
{
    void Initialize(FeedPeekPanelSession session);

    Task ActivateAsync(CancellationToken cancellationToken = default);

    void Deactivate();
}
