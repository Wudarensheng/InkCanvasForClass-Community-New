using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Shapes;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace Ink_Canvas.Helpers
{
    /// <summary>
    /// 多人协作管理器，处理实时同步功能
    /// </summary>
    public class CollaborationManager
    {
        private ClientWebSocket _webSocket;
        private bool _isConnected = false;
        private string _sessionId;
        private string _serverUrl;
        private CancellationTokenSource _cancellationTokenSource;

        // 本地用户标识
        private string _localUserId;
        private string _userName;

        // 事件委托
        public event Action<Stroke> OnRemoteStrokeAdded;
        public event Action<Guid> OnRemoteStrokeDeleted;
        public event Action<Point, string> OnRemoteCursorMoved;
        public event Action<string, string> OnUserJoined;
        public event Action<string> OnUserLeft;
        public event Action<string> OnConnectionStatusChanged;

        public bool IsConnected => _isConnected;
        public string SessionId => _sessionId;

        public CollaborationManager()
        {
            _localUserId = Guid.NewGuid().ToString();
            _userName = Environment.UserName ?? "Anonymous";
        }

        /// <summary>
        /// 连接到协作服务器
        /// </summary>
        public async Task<bool> ConnectToServer(string serverUrl, string sessionId = null)
        {
            try
            {
                _serverUrl = serverUrl;
                _sessionId = sessionId ?? Guid.NewGuid().ToString();
                _cancellationTokenSource = new CancellationTokenSource();

                _webSocket = new ClientWebSocket();
                
                var uri = new Uri($"{serverUrl}?sessionId={_sessionId}&userId={_localUserId}&userName={_userName}");
                await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);

                _isConnected = true;
                OnConnectionStatusChanged?.Invoke("已连接到协作服务器");

                // 启动消息接收循环
                _ = Task.Run(ReceiveMessagesLoop);

                // 发送加入消息
                var joinMessage = new CollaborativeMessage
                {
                    Type = MessageType.UserJoined,
                    SessionId = _sessionId,
                    UserId = _localUserId,
                    UserName = _userName,
                    Timestamp = DateTime.UtcNow
                };
                await SendMessageAsync(joinMessage);

                return true;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"连接协作服务器失败: {ex.Message}", LogHelper.LogType.Error);
                _isConnected = false;
                OnConnectionStatusChanged?.Invoke($"连接失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                var leaveMessage = new CollaborativeMessage
                {
                    Type = MessageType.UserLeft,
                    SessionId = _sessionId,
                    UserId = _localUserId,
                    Timestamp = DateTime.UtcNow
                };
                await SendMessageAsync(leaveMessage);

                _cancellationTokenSource?.Cancel();
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected", CancellationToken.None);
            }
            
            _isConnected = false;
            OnConnectionStatusChanged?.Invoke("已断开协作服务器");
        }

        /// <summary>
        /// 接收消息循环
        /// </summary>
        private async Task ReceiveMessagesLoop()
        {
            var buffer = new byte[8192];
            try
            {
                while (_webSocket.State == WebSocketState.Open && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var jsonString = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        var message = JsonConvert.DeserializeObject<CollaborativeMessage>(jsonString);

                        await HandleIncomingMessage(message);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _isConnected = false;
                        OnConnectionStatusChanged?.Invoke("与协作服务器的连接已关闭");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"接收消息时出错: {ex.Message}", LogHelper.LogType.Error);
                _isConnected = false;
                OnConnectionStatusChanged?.Invoke($"接收消息出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理接收到的消息
        /// </summary>
        private async Task HandleIncomingMessage(CollaborativeMessage message)
        {
            switch (message.Type)
            {
                case MessageType.StrokeAdded:
                    if (message.UserId != _localUserId) // 忽略自己的消息
                    {
                        var strokeData = JsonConvert.DeserializeObject<StrokeData>(message.Data.ToString());
                        var stroke = DeserializeStroke(strokeData);
                        if (stroke != null)
                        {
                            OnRemoteStrokeAdded?.Invoke(stroke);
                        }
                    }
                    break;

                case MessageType.StrokeRemoved:
                    if (message.UserId != _localUserId)
                    {
                        var strokeId = Guid.Parse(message.Data.ToString());
                        OnRemoteStrokeDeleted?.Invoke(strokeId);
                    }
                    break;

                case MessageType.CursorMoved:
                    if (message.UserId != _localUserId)
                    {
                        var cursorData = JsonConvert.DeserializeObject<CursorData>(message.Data.ToString());
                        OnRemoteCursorMoved?.Invoke(cursorData.Position, message.UserName);
                    }
                    break;

                case MessageType.UserJoined:
                    if (message.UserId != _localUserId)
                    {
                        OnUserJoined?.Invoke(message.UserId, message.UserName);
                    }
                    break;

                case MessageType.UserLeft:
                    if (message.UserId != _localUserId)
                    {
                        OnUserLeft?.Invoke(message.UserId);
                    }
                    break;
            }
        }

        /// <summary>
        /// 发送消息到服务器
        /// </summary>
        private async Task SendMessageAsync(CollaborativeMessage message)
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            var jsonString = JsonConvert.SerializeObject(message);
            var bytes = Encoding.UTF8.GetBytes(jsonString);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// 添加本地笔迹并同步到其他用户
        /// </summary>
        public async Task AddStrokeAsync(Stroke stroke)
        {
            var strokeData = SerializeStroke(stroke);
            var message = new CollaborativeMessage
            {
                Type = MessageType.StrokeAdded,
                SessionId = _sessionId,
                UserId = _localUserId,
                UserName = _userName,
                Data = strokeData,
                Timestamp = DateTime.UtcNow
            };

            await SendMessageAsync(message);
        }

        /// <summary>
        /// 删除本地笔迹并同步到其他用户
        /// </summary>
        public async Task RemoveStrokeAsync(Guid strokeId)
        {
            var message = new CollaborativeMessage
            {
                Type = MessageType.StrokeRemoved,
                SessionId = _sessionId,
                UserId = _localUserId,
                UserName = _userName,
                Data = strokeId.ToString(),
                Timestamp = DateTime.UtcNow
            };

            await SendMessageAsync(message);
        }

        /// <summary>
        /// 移动光标并同步到其他用户
        /// </summary>
        public async Task MoveCursorAsync(Point position)
        {
            var cursorData = new CursorData
            {
                Position = position
            };

            var message = new CollaborativeMessage
            {
                Type = MessageType.CursorMoved,
                SessionId = _sessionId,
                UserId = _localUserId,
                UserName = _userName,
                Data = cursorData,
                Timestamp = DateTime.UtcNow
            };

            await SendMessageAsync(message);
        }

        /// <summary>
        /// 序列化笔迹为数据
        /// </summary>
        private StrokeData SerializeStroke(Stroke stroke)
        {
            return new StrokeData
            {
                Id = stroke.DrawingAttributes.GetHashCode().ToString(), // 实际上应该使用唯一ID
                StylusPoints = stroke.StylusPoints.Select(sp => new StylusPointData
                {
                    X = sp.X,
                    Y = sp.Y,
                    PressureFactor = sp.PressureFactor
                }).ToArray(),
                DrawingAttributes = new DrawingAttributesData
                {
                    Color = stroke.DrawingAttributes.Color.ToString(),
                    Width = stroke.DrawingAttributes.Width,
                    Height = stroke.DrawingAttributes.Height,
                    IsHighlighter = stroke.DrawingAttributes.IsHighlighter,
                    FitToCurve = stroke.DrawingAttributes.FitToCurve
                }
            };
        }

        /// <summary>
        /// 从数据反序列化笔迹
        /// </summary>
        private Stroke DeserializeStroke(StrokeData strokeData)
        {
            try
            {
                var stylusPoints = strokeData.StylusPoints.Select(sp => 
                    new StylusPoint(sp.X, sp.Y, sp.PressureFactor)).ToList();

                var color = System.Windows.Media.Colors.Black; // 默认颜色
                if (!string.IsNullOrEmpty(strokeData.DrawingAttributes.Color))
                {
                    try
                    {
                        color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(strokeData.DrawingAttributes.Color);
                    }
                    catch
                    {
                        // 如果颜色转换失败，保持默认颜色
                    }
                }

                var drawingAttributes = new DrawingAttributes
                {
                    Color = color,
                    Width = strokeData.DrawingAttributes.Width,
                    Height = strokeData.DrawingAttributes.Height,
                    IsHighlighter = strokeData.DrawingAttributes.IsHighlighter,
                    FitToCurve = strokeData.DrawingAttributes.FitToCurve
                };

                var stroke = new Stroke(new StylusPointCollection(stylusPoints))
                {
                    DrawingAttributes = drawingAttributes
                };

                return stroke;
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"反序列化笔迹失败: {ex.Message}", LogHelper.LogType.Error);
                return null;
            }
        }
    }

    #region 数据模型

    /// <summary>
    /// 消息类型枚举
    /// </summary>
    public enum MessageType
    {
        StrokeAdded,
        StrokeRemoved,
        CursorMoved,
        UserJoined,
        UserLeft
    }

    /// <summary>
    /// 协作消息类
    /// </summary>
    public class CollaborativeMessage
    {
        [JsonProperty("type")]
        public MessageType Type { get; set; }

        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("userId")]
        public string UserId { get; set; }

        [JsonProperty("userName")]
        public string UserName { get; set; }

        [JsonProperty("data")]
        public object Data { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// 笔迹数据类
    /// </summary>
    public class StrokeData
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("stylusPoints")]
        public StylusPointData[] StylusPoints { get; set; }

        [JsonProperty("drawingAttributes")]
        public DrawingAttributesData DrawingAttributes { get; set; }
    }

    /// <summary>
    /// 笔尖点数据类
    /// </summary>
    public class StylusPointData
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }

        [JsonProperty("pressureFactor")]
        public float PressureFactor { get; set; }
    }

    /// <summary>
    /// 绘图属性数据类
    /// </summary>
    public class DrawingAttributesData
    {
        [JsonProperty("color")]
        public string Color { get; set; }

        [JsonProperty("width")]
        public double Width { get; set; }

        [JsonProperty("height")]
        public double Height { get; set; }

        [JsonProperty("isHighlighter")]
        public bool IsHighlighter { get; set; }

        [JsonProperty("fitToCurve")]
        public bool FitToCurve { get; set; }
    }

    /// <summary>
    /// 光标数据类
    /// </summary>
    public class CursorData
    {
        [JsonProperty("position")]
        public Point Position { get; set; }
    }

    #endregion
}