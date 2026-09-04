using FinXmlProcessor.Worker;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await CliApp.RunAsync(args, Console.Out, Console.Error, cancellationToken: cts.Token).ConfigureAwait(false);
