using GamenTrail.Core.Audio;
using GamenTrail.Platform.Windows.Audio;
foreach(var rate in new[]{44100,48000})
{
 await using var c=new WasapiLoopbackCapture();
 await c.InitializeAsync(new AudioCaptureOptions(SampleRate:rate, SampleFormat:AudioSampleFormat.SignedInteger, BitsPerSample:16, ChannelCount:2),CancellationToken.None);
 long samples=0; double first=-1,last=0;int count=0;
 c.PacketArrived+=(_,p)=>{using(p){if(first<0)first=p.Timestamp.TotalSeconds;last=p.Timestamp.TotalSeconds+p.Duration.TotalSeconds;samples+=p.Buffer.Length/4;count++;}};
 await c.StartAsync(CancellationToken.None);await Task.Delay(3000);await c.StopAsync(CancellationToken.None);
 Console.WriteLine($"rate={rate} packets={count} samples={samples} PCM_seconds={(double)samples/rate:F6} timeline_seconds={last-first:F6}");
}