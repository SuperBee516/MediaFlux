using MediaFlux.Services.EncodingV2;
using MediaFlux.Services.EncodingV2Gui;

namespace MediaFlux.Services;

/// <summary>Applies the GUI production capability boundary to one headless saved-job item.</summary>
public sealed class HeadlessSavedJobV2Router
{
    private readonly IV2GuiProductionAdapter _adapter;

    public HeadlessSavedJobV2Router(IV2GuiProductionAdapter adapter) =>
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public Task<V2GuiPreparationResult> PrepareAsync(
        V2GuiEncodeIntent intent,
        CancellationToken cancellationToken)
    {
        string? unsupportedReason = V2GuiRequestMapper.GetIntentUnsupportedReason(intent);
        return unsupportedReason is not null
            ? Task.FromResult(V2GuiPreparationResult.Unsupported(unsupportedReason))
            : _adapter.PrepareAsync(intent, cancellationToken);
    }

    public async Task<HeadlessSavedJobV2RouteResult> ExecuteAsync(
        V2GuiEncodeIntent intent,
        Action<V2EncodingQueueEvent>? eventObserver,
        Action<V2GuiPreparedEncode>? v2Selected,
        CancellationToken cancellationToken)
    {
        V2GuiPreparationResult preparation;
        try
        {
            preparation = await PrepareAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(null, null, "V2 capability preflight failed before production: " + ex.Message, null);
        }

        if (!preparation.Supported || preparation.Prepared is null)
            return new(null, null, preparation.Reason, null);

        V2GuiPreparedEncode prepared = preparation.Prepared;
        try { v2Selected?.Invoke(prepared); }
        catch { /* Routing diagnostics cannot alter the selected production engine. */ }

        try
        {
            V2GuiQueueItemResult result = await RunAsync(prepared, eventObserver, cancellationToken).ConfigureAwait(false);
            return new(prepared, result, "", null);
        }
        catch (Exception ex)
        {
            // Once V2 is selected, a run failure is terminal for this item. It must never fall through to V1.
            return new(prepared, null, "", ex);
        }
    }

    public Task<V2GuiQueueItemResult> RunAsync(
        V2GuiPreparedEncode prepared,
        Action<V2EncodingQueueEvent>? eventObserver,
        CancellationToken cancellationToken) =>
        _adapter.RunAsync(prepared, eventObserver, cancellationToken);
}

public sealed record HeadlessSavedJobV2RouteResult(
    V2GuiPreparedEncode? Prepared,
    V2GuiQueueItemResult? Run,
    string FallbackReason,
    Exception? ExecutionException);
