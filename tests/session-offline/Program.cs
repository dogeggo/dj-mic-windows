using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using MicMini.Desktop.Protocol;
using MicMini.Desktop.Services;

var checks=0;
void Check(bool okay,string name){if(!okay)throw new Exception("FAIL: "+name); checks++;}
byte[] Payload(byte kind,uint selector,byte[] data){var b=new byte[data.Length+9];b[0]=3;BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1),(ushort)(data.Length+6));b[3]=kind;BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4),selector);b[8]=(byte)data.Length;data.CopyTo(b,9);return b;}
var rx=Convert.FromHexString("200500000000000000000000000000000000000000000000010000003e000000");
var tx=Convert.FromHexString("1831040100780000000000000000000000000000000000000000");
byte[] cap=[];
foreach(var line in File.ReadLines(args.Length > 0 ? args[0] : "analysis/evidence/mic-mini-bulk-readonly.log")){
 if(!line.StartsWith("FRAME "))continue;
 using var doc=JsonDocument.Parse(line[6..]);var e=doc.RootElement;
 if(e.GetProperty("CommandSet").GetInt32()==0x5b&&e.GetProperty("Command").GetInt32()==0x12){cap=e.GetProperty("Payload").GetBytesFromBase64();break;}
}
Check(cap.Length>0,"saved capability fixture present");
await using var session=new MicBulkSession();
Check(!session.HasConnectedTransmitter,"uninitialized session cannot enable recording");
var transport=WinUsbTransport.Latest!;
void Push(byte kind,uint sel,byte[] data)=>transport.Inject(new(0x5a,2,0,0,0x5b,3,Payload(kind,sel,data)));
void Ack(DumlFrame request, byte code=0)=>transport.Inject(new(request.Receiver,request.Sender,request.Sequence,0x80,request.CommandSet,request.Command,[code]));
transport.Send= request=>{
 Check(request.Command==0x12,"initial query only");
 transport.Inject(new(0x5a,2,request.Sequence,0x80,0x5b,0x12,cap));
 Push(2,1,tx);Push(3,0,rx); // Deliberately TX before RX.
 Push(1,1,Convert.FromHexString("001103023850445750384b30313531355841"));
 Push(4,1,"f99fe3"u8.ToArray());Push(6,1,"DJI Mic Mini"u8.ToArray());
 return Task.CompletedTask;
};
await session.ConnectAndQueryCapabilitiesAsync();
Check(session.IsOpen&&session.Receiver!=null&&session.Transmitters.ContainsKey(1),"initial live state ready");
Check(session.HasConnectedTransmitter,"fresh connected TX enables recording");
Check(session.Identities[1].FirmwareVersion=="02.03.17.00"&&session.Identities[1].DeviceName=="f99fe3"&&session.Identities[1].ProductName=="DJI Mic Mini","identity incremental merge");
var used=new HashSet<ushort>();
transport.Send=request=>{
 Check(used.Add(request.Sequence),"sequence never reused");
 Check(request.Payload.SequenceEqual(new byte[]{2,1,0,0,0,3,0,1,1}),"mode2 selector32 one-byte TLV");
 transport.Inject(new(0x5a,2,(ushort)(request.Sequence+100),0x80,0x5b,1,[0]));
 transport.Inject(new(0x5a,3,request.Sequence,0x80,0x5b,1,[0]));
 transport.Inject(new(0x5a,2,request.Sequence,0x00,0x5b,1,[0]));
 Ack(request);tx[3]|=0x20;Push(2,1,tx);
 return Task.CompletedTask;
};
var gate=typeof(MicBulkSession).GetField("gate",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session)!;
await session.SetValueAsync(1,3,1,()=>{Check(!Monitor.IsEntered(gate),"readback predicate outside state lock");return session.Transmitters[1].LowCut;});
Check(session.Transmitters[1].LowCut,"new target state confirms setter");
async Task MustWait(string name,Action<DumlFrame> action,Func<bool>? predicate=null){
 transport.Send=request=>{Check(used.Add(request.Sequence),"sequence never reused");action(request);return Task.CompletedTask;};
 using var timeout=new CancellationTokenSource(100);
 try{await session.SetValueAsync(1,3,1,predicate??(()=>true),timeout.Token);throw new Exception("Unexpected completion: "+name);}
 catch(OperationCanceledException) when(timeout.IsCancellationRequested){checks++;}
}
await MustWait("ACK alone is insufficient",request=>Ack(request));
await MustWait("wrong selector state is insufficient",request=>{Ack(request);Push(3,0,rx);});
await MustWait("identity does not satisfy readback",request=>{Ack(request);Push(4,1,"f99fe3"u8.ToArray());});
await MustWait("meter does not satisfy readback",request=>{Ack(request);Push(5,1,[12]);});
await MustWait("mismatched ACK is rejected",request=>{transport.Inject(new(0x5a,2,(ushort)(request.Sequence+10),0x80,0x5b,1,[0]));Push(2,1,tx);});
await MustWait("wrong response address is rejected",request=>{transport.Inject(new(0x5a,3,request.Sequence,0x80,0x5b,1,[0]));Push(2,1,tx);});
await MustWait("missing response bit is rejected",request=>{transport.Inject(new(0x5a,2,request.Sequence,0x00,0x5b,1,[0]));Push(2,1,tx);});
await MustWait("wrong response command is rejected",request=>{transport.Inject(new(0x5a,2,request.Sequence,0x80,0x5b,2,[0]));Push(2,1,tx);});
await MustWait("false predicate is insufficient",request=>{Ack(request);Push(2,1,tx);},()=>false);
transport.Send=request=>{Ack(request,7);Push(2,1,tx);return Task.CompletedTask;};
try{await session.SetValueAsync(1,3,1,()=>true);throw new Exception("negative ACK accepted");}catch(IOException){checks++;}
transport.Send=request=>{Ack(request);rx[24]=0;Push(3,0,rx);return Task.CompletedTask;};
try{await session.SetValueAsync(1,3,1,()=>true);throw new Exception("offline target accepted");}catch(IOException){checks++;}
Check(session.Transmitters.Count==0&&!session.Identities.ContainsKey(1),"offline TX removes cached identity");
Check(!session.HasConnectedTransmitter,"receiver-only connection cannot enable recording");
Push(2,1,tx);
Check(!session.HasConnectedTransmitter,"late offline TX status cannot enable recording");
var sends=transport.SendCount;
try{await session.SetValueAsync(1,3,1,()=>true);throw new Exception("offline write dispatched");}catch(IOException){checks++;}
Check(transport.SendCount==sends,"offline target rejected before I/O");
Push(1,1,Convert.FromHexString("001103023850445750384b30313531355841"));
Check(!session.Identities.ContainsKey(1),"late offline identity ignored");
rx[24]=1;Push(3,0,rx);Push(2,1,tx);
Check(session.HasConnectedTransmitter,"fresh reconnected TX restores recording availability");
Check(!session.Identities.ContainsKey(1),"replacement live status cannot reuse old serial");
Push(1,1,Convert.FromHexString("001103023850445750384b30313531355842"));
Check(session.Identities[1].SerialNumber=="8PDWP8K01515XB"&&session.Identities[1].ProductName==null,"replacement needs complete new identity");
Push(6,1,"DJI Mic Mini"u8.ToArray());
Check(session.Identities[1].ProductName=="DJI Mic Mini","replacement product read afresh");
Push(1,1,Convert.FromHexString("001103023850445750384b30313531355843"));
Check(session.Identities[1].ProductName==null,"serial change without mask change cannot retain old fields");
Push(6,1,"DJI Mic Mini"u8.ToArray());
var times=(Dictionary<uint,long>)typeof(MicBulkSession).GetField("transmitterTimes",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session)!;
lock(gate) times[1]=System.Diagnostics.Stopwatch.GetTimestamp()-4*System.Diagnostics.Stopwatch.Frequency;
Check(!session.HasConnectedTransmitter,"expired TX status disables recording before expiry timer");
Check(!session.Identities.ContainsKey(1),"expired identity hidden before timer fires");
Push(2,1,tx);
Check(!session.Identities.ContainsKey(1),"fresh TX status after expiration cannot revive cached serial");
Push(1,1,Convert.FromHexString("001103023850445750384b30313531355843"));
typeof(MicBulkSession).GetField("receiverTime",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(session,System.Diagnostics.Stopwatch.GetTimestamp()-4*System.Diagnostics.Stopwatch.Frequency);
Check(!session.HasConnectedTransmitter,"expired RX status disables recording despite fresh TX");
try{await session.SetValueAsync(0,0x1e,1,()=>true);throw new Exception("stale RX write dispatched");}catch(IOException){checks++;}
Check(transport.SendCount==sends,"stale target rejected before I/O");
Check(session.Identities.Count==0,"stale receiver hides identities immediately");
Push(3,0,rx);Push(2,1,tx);
Check(session.Identities.Count==0,"fresh receiver after expiry does not revive identities");
rx[24]=3;Push(3,0,rx);Push(2,2,tx);
rx[24]=2;Push(3,0,rx);
Check(session.HasConnectedTransmitter,"remaining online TX keeps recording available after another disconnects");
rx[24]=0;Push(3,0,rx);
Check(!session.HasConnectedTransmitter,"last TX disconnect disables recording");
rx[24]=1;Push(3,0,rx);Push(2,1,tx);
transport.Disconnect();
Check(!session.HasConnectedTransmitter,"transport disconnect disables recording immediately");
await session.DisposeAsync();
Check(!session.IsOpen&&session.Receiver==null&&session.Transmitters.Count==0&&session.Capabilities==null,"disposed state invalidated");
Check(!session.HasConnectedTransmitter,"disposed session cannot enable recording");
Console.WriteLine($"PASS: {checks} assertions; fake WinUSB transport only, no device access.");

namespace MicMini.Desktop.Services {
 public sealed record WinUsbDeviceInfo(string Path,string InstanceId,string Service);
 public sealed class WinUsbTransport:IAsyncDisposable {
  public static WinUsbTransport? Latest;public bool IsOpen{get;private set;}public Func<DumlFrame,Task>? Send;public int SendCount;
  public event Action<byte[]>? DataReceived;public event Action<string>? Disconnected;
  public WinUsbTransport(){Latest=this;}
  public static IReadOnlyList<WinUsbDeviceInfo> Enumerate()=>[new("offline","offline","WinUSB")];
  public void Open(WinUsbDeviceInfo device){IsOpen=true;}
  public Task SendAsync(ReadOnlyMemory<byte> bytes,CancellationToken token=default){token.ThrowIfCancellationRequested();SendCount++;return Send!(DumlCodec.Decode(bytes.Span));}
  public void Inject(DumlFrame frame)=>DataReceived?.Invoke(DumlCodec.Encode(frame));
  public void Disconnect(){IsOpen=false;Disconnected?.Invoke("offline test disconnect");}
  public ValueTask DisposeAsync(){IsOpen=false;return ValueTask.CompletedTask;}
 }
}

