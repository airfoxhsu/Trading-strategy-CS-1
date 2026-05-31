using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using ExtremeSignalAppCS.Models;

namespace ExtremeSignalAppCS.Services
{
    /// <summary>
    /// COM 屬性通知連接點 Dummy 介面。
    /// </summary>
    [ComImport]
    [Guid("9BFBBC02-EFF1-101a-8574-00DD010F2FFA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyNotifySink
    {
        /// <summary>
        /// 當屬性變更時回呼
        /// </summary>
        [PreserveSig]
        int OnChanged(int dispID);
        /// <summary>
        /// 請求編輯屬性時回呼
        /// </summary>
        [PreserveSig]
        int OnRequestEdit(int dispID);
    }

    /// <summary>
    /// 動態 ProgID 承載 AxHost。
    /// 藉由此類別，我們不需要在編譯期依賴元大 Quote 內部 Interop DLL，
    /// 而能在執行期動態獲取其 CLSID 並將 ActiveX 穩定創建在 WPF / WinForms 宿主上。
    /// </summary>
    public class AxYuantaQuoteHost : AxHost
    {
        /// <summary>
        /// 初始化 AxHost，傳入 CLSID 字串
        /// </summary>
        /// <param name="clsid">元大 COM CLSID</param>
        public AxYuantaQuoteHost(string clsid) : base(clsid)
        {
        }

        /// <summary>
        /// 獲取底層 Ocx 元件。
        /// </summary>
        public object GetOcxInstance()
        {
            return this.GetOcx() ?? throw new InvalidOperationException("無法獲取底層元大行情 OCX 元件實體，元件未正確初始化。");
        }
    }

    /// <summary>
    /// 行情全部資訊接收委派
    /// </summary>
    public delegate void GetMktAllReceivedDelegate(
        string symbol, string refPri, string openPri, string highPri, string lowPri,
        string upPri, string dnPri, string matchTime, string matchPri, string matchQty,
        string tolMatchQty, string bestBuyQty, string bestBuyPri, string bestSellQty, string bestSellPri,
        string fdbPri, string fdbQty, string fdsPri, string fdsQty, int reqType);

    /// <summary>
    /// 元大行情 COM ActiveX 互操作封裝層。
    /// </summary>
    public class YuantaQuoteWrapper
    {
        private readonly AxYuantaQuoteHost _axHost;
        private readonly object _ocx;
        private readonly Type _ocxType;
        
        // COM 事件 ConnectionPoint cookie
        private int _cookie = -1;
        private IConnectionPoint? _connectionPoint;
        private YuantaQuoteEventsSink? _eventsSink;

        // 當 COM 事件觸發時，通知主介面的委派事件
        public event Action<int, string, int>? MktStatusChanged;
        public event GetMktAllReceivedDelegate? GetMktAllReceived;

        /// <summary>
        /// 建構元大行情 Wrapper
        /// </summary>
        /// <param name="axHost">AxHost 宿主控制項</param>
        public YuantaQuoteWrapper(AxYuantaQuoteHost axHost)
        {
            _axHost = axHost;
            _ocx = axHost.GetOcxInstance();
            _ocxType = _ocx.GetType();
            
            // 訂閱 COM 連接點事件
            ConnectEvents();
        }

        /// <summary>
        /// 動態呼叫元大行情登入方法。
        /// </summary>
        public int SetMktLogon(string user, string pwd, string ip, string port, int mode, int localLp)
        {
            try
            {
                // 元大 API: SetMktLogon(user, pwd, ip, port, mode, localLp)
                object? res = _ocxType.InvokeMember("SetMktLogon", 
                    BindingFlags.InvokeMethod, 
                    null, 
                    _ocx, 
                    new object[] { user, pwd, ip, port, mode, localLp });
                
                return res is int i ? i : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM] 呼叫 SetMktLogon 失敗: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 動態呼叫商品註冊監聽方法。
        /// </summary>
        public int AddMktReg(string symbol, int mode, int reqType, int param)
        {
            try
            {
                // 元大 API: AddMktReg(symbol, mode, reqType, param)
                object? res = _ocxType.InvokeMember("AddMktReg", 
                    BindingFlags.InvokeMethod, 
                    null, 
                    _ocx, 
                    new object[] { symbol, mode, reqType, param });
                
                return res is int i ? i : 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM] 呼叫 AddMktReg 失敗: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 連接 COM 事件接收源 (ConnectionPoint)。
        /// </summary>
        private void ConnectEvents()
        {
            try
            {
                var container = (IConnectionPointContainer)_ocx;
                
                // 元大行情的事件 Interface Guid (自 YUANTAQUOTE 元件的 TypeLib 查出，或動態搜尋)
                // 一般來說，我們可以動態獲取 IConnectionPointContainer 裡所有的 ConnectionPoints 
                // 並挑選非 IUnknown / IDispatch 的那一個，這在 C# 中非常通用且穩定。
                container.EnumConnectionPoints(out IEnumConnectionPoints enumPoints);
                IConnectionPoint[] points = new IConnectionPoint[1];
                IntPtr fetched = IntPtr.Zero;

                while (enumPoints.Next(1, points, fetched) == 0)
                {
                    points[0].GetConnectionInterface(out Guid iid);
                    // 找到元大行情的 Events Interface (非標準的 IPropertyNotifySink)
                    if (iid != typeof(IPropertyNotifySink).GUID && iid != Guid.Empty)
                    {
                        _connectionPoint = points[0];
                        break;
                    }
                }

                if (_connectionPoint != null)
                {
                    _eventsSink = new YuantaQuoteEventsSink(this);
                    _connectionPoint.Advise(_eventsSink, out _cookie);
                    Console.WriteLine("[COM] 成功綁定元大行情 API 連接點事件！");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM] 綁定事件接收器失敗: {ex.Message}。將回退至動態 Property 監聽。");
            }
        }

        /// <summary>
        /// 斷開 COM 事件接收點，斬斷殘餘 Tick 連線，徹底消滅幽靈事件。
        /// </summary>
        public void DisconnectEvents()
        {
            try
            {
                if (_connectionPoint != null && _cookie != -1)
                {
                    _connectionPoint.Unadvise(_cookie);
                    _cookie = -1;
                    _connectionPoint = null;
                }
                _eventsSink = null;
                Console.WriteLine("[COM] 已斷開元大行情連線點事件。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[COM] 斷開事件連線出錯: {ex.Message}");
            }
        }

        // 內部方法，用於將 Sink 事件導出至外部 Wrapper
        internal void RaiseMktStatusChanged(int status, string msg, int reqType)
        {
            MktStatusChanged?.Invoke(status, msg, reqType);
        }

        internal void RaiseGetMktAllReceived(
            string symbol, string refPri, string openPri, string highPri, string lowPri,
            string upPri, string dnPri, string matchTime, string matchPri, string matchQty,
            string tolMatchQty, string bestBuyQty, string bestBuyPri, string bestSellQty, string bestSellPri,
            string fdbPri, string fdbQty, string fdsPri, string fdsQty, int reqType)
        {
            GetMktAllReceived?.Invoke(
                symbol, refPri, openPri, highPri, lowPri,
                upPri, dnPri, matchTime, matchPri, matchQty,
                tolMatchQty, bestBuyQty, bestBuyPri, bestSellQty, bestSellPri,
                fdbPri, fdbQty, fdsPri, fdsQty, reqType);
        }
    }

    /// <summary>
    /// COM IDispatch 事件接收槽實作。
    /// 元大 API 在拋出事件時，會調用 IDispatch.Invoke，並傳入對應的 DispID。
    /// 我們透過手動繼承 IReflect 或實作自訂的自訂調用，來無縫接收元大 API 發射的 Tick！
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class YuantaQuoteEventsSink : IReflect
    {
        private readonly YuantaQuoteWrapper _wrapper;

        /// <summary>
        /// 建構事件接收器實體
        /// </summary>
        /// <param name="wrapper">元大 Wrapper</param>
        public YuantaQuoteEventsSink(YuantaQuoteWrapper wrapper)
        {
            _wrapper = wrapper;
        }

        // IReflect 核心實作：元大 COM 會呼叫此 InvokeMember 以分發事件！
        /// <summary>
        /// 反射呼叫實作，接收並分發 COM 事件
        /// </summary>
        public object? InvokeMember(string name, BindingFlags invokeAttr, Binder? binder, object? target, object?[]? args, ParameterModifier[]? modifiers, System.Globalization.CultureInfo? culture, string[]? namedParameters)
        {
            // 將所有收到的 COM 事件以更精練的方式輸出，以不區分大小寫匹配
            string upperName = name.ToUpperInvariant();

            // DispID = 1 是 OnMktStatusChange(short Status, string Msg, short ReqType)
            if (upperName.Contains("DISPID=1") || upperName.Contains("MKTSTATUSCHANGE"))
            {
                if (args != null && args.Length >= 2)
                {
                    int status = Convert.ToInt32(args[0]);
                    string msg = args.Length >= 2 ? args[1]?.ToString() ?? "" : "";
                    int reqType = args.Length >= 3 && args[2] != null ? Convert.ToInt32(args[2]) : 0;
                    
                    Console.WriteLine($"[COM事件] 登入狀態變更 - status: {status}, msg: {msg}, reqType: {reqType}");
                    _wrapper.RaiseMktStatusChanged(status, msg, reqType);
                }
            }
            // DispID = 2 是 OnGetMktAll(...)
            else if (upperName.Contains("DISPID=2") || upperName.Contains("GETMKTALL"))
            {
                if (args != null && args.Length >= 15)
                {
                    string symbol = args[0]?.ToString() ?? "";
                    string refPri = args.Length > 1 ? args[1]?.ToString() ?? "" : "";
                    string openPri = args.Length > 2 ? args[2]?.ToString() ?? "" : "";
                    string highPri = args.Length > 3 ? args[3]?.ToString() ?? "" : "";
                    string lowPri = args.Length > 4 ? args[4]?.ToString() ?? "" : "";
                    string upPri = args.Length > 5 ? args[5]?.ToString() ?? "" : "";
                    string dnPri = args.Length > 6 ? args[6]?.ToString() ?? "" : "";
                    string matchTime = args.Length > 7 ? args[7]?.ToString() ?? "" : "";
                    string matchPri = args.Length > 8 ? args[8]?.ToString() ?? "" : "";
                    string matchQty = args.Length > 9 ? args[9]?.ToString() ?? "" : "";
                    string tolMatchQty = args.Length > 10 ? args[10]?.ToString() ?? "" : "";
                    string bestBuyQty = args.Length > 11 ? args[11]?.ToString() ?? "" : "";
                    string bestBuyPri = args.Length > 12 ? args[12]?.ToString() ?? "" : "";
                    string bestSellQty = args.Length > 13 ? args[13]?.ToString() ?? "" : "";
                    string bestSellPri = args.Length > 14 ? args[14]?.ToString() ?? "" : "";
                    string fdbPri = args.Length > 15 ? args[15]?.ToString() ?? "" : "";
                    string fdbQty = args.Length > 16 ? args[16]?.ToString() ?? "" : "";
                    string fdsPri = args.Length > 17 ? args[17]?.ToString() ?? "" : "";
                    string fdsQty = args.Length > 18 ? args[18]?.ToString() ?? "" : "";
                    int reqType = args.Length > 19 ? Convert.ToInt32(args[19]) : 0;

                    _wrapper.RaiseGetMktAllReceived(
                        symbol, refPri, openPri, highPri, lowPri,
                        upPri, dnPri, matchTime, matchPri, matchQty,
                        tolMatchQty, bestBuyQty, bestBuyPri, bestSellQty, bestSellPri,
                        fdbPri, fdbQty, fdsPri, fdsQty, reqType);
                }
            }
            return null;
        }

        // 以下為 IReflect 與自訂調用的樣板實作，全部導向預設
        /// <summary>
        /// 獲取欄位說明
        /// </summary>
        public FieldInfo? GetField(string name, BindingFlags bindingAttr) => null;
        /// <summary>
        /// 獲取所有欄位
        /// </summary>
        public FieldInfo[] GetFields(BindingFlags bindingAttr) => Array.Empty<FieldInfo>();
        /// <summary>
        /// 獲取成員說明
        /// </summary>
        public MemberInfo[] GetMember(string name, BindingFlags bindingAttr) => Array.Empty<MemberInfo>();
        /// <summary>
        /// 獲取所有成員
        /// </summary>
        public MemberInfo[] GetMembers(BindingFlags bindingAttr) => Array.Empty<MemberInfo>();
        /// <summary>
        /// 獲取方法說明
        /// </summary>
        public MethodInfo? GetMethod(string name, BindingFlags bindingAttr) => null;
        /// <summary>
        /// 獲取特定簽章之方法
        /// </summary>
        public MethodInfo? GetMethod(string name, BindingFlags bindingAttr, Binder? binder, Type[] types, ParameterModifier[]? modifiers) => null;
        /// <summary>
        /// 獲取所有方法
        /// </summary>
        public MethodInfo[] GetMethods(BindingFlags bindingAttr) => Array.Empty<MethodInfo>();
        /// <summary>
        /// 獲取所有屬性
        /// </summary>
        public PropertyInfo[] GetProperties(BindingFlags bindingAttr) => Array.Empty<PropertyInfo>();
        /// <summary>
        /// 獲取特定屬性
        /// </summary>
        public PropertyInfo? GetProperty(string name, BindingFlags bindingAttr) => null;
        /// <summary>
        /// 獲取特定屬性
        /// </summary>
        public PropertyInfo? GetProperty(string name, BindingFlags bindingAttr, Binder? binder, Type? returnType, Type[] types, ParameterModifier[]? modifiers) => null;
        /// <summary>
        /// 獲取底層系統型別
        /// </summary>
        public Type UnderlyingSystemType => typeof(YuantaQuoteEventsSink);
    }
}
