using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace SmartHomeLightSwitcher
{
    public partial class MainWindow : Window
    {
        // Размер буфера для принимаемых данных.
        private static readonly int BUFFER_SIZE = 8192;
        // Разделитель между аргументом и значением элемента данных.
        private static readonly char DELIMITER = ';';
        // Надпись элемента интерфейса.
        private static readonly string IPADDRESS_LOG_LABEL = "IP Address: ";
        // Надпись элемента интерфейса.
        private static readonly string PORT_LOG_LABEL = "Port: ";
        // Минимальное и максимальное значения используемого порта.
        private static readonly int MINIMAL_PORT_VALUE = 1024;
        private static readonly int MAXIMAL_PORT_VALUE = 49151;
        // Надпись элемента интерфейса.
        private static readonly string CONNECTION_LOG_LABEL = "Connection: ";
        // Состояния подключения.
        private static readonly string CONNECTION_UP = "up";
        private static readonly string CONNECTION_WAIT = "wait";
        private static readonly string CONNECTION_DOWN = "down";
        private static readonly string CONNECTION_ERR = "err";
        // Метка устройства для журнала.
        private static readonly string LIGHT_SWITCHER_LOG_LABEL = "Light Switcher: ";
        // Метка сети для журнала.
        private static readonly string NETWORK_LOG_LABEL = "Network: ";
        // Аргумент типа устройства.
        private static readonly string NETWORK_DEVICE_ARG = "Device: ";
        // Аргумент состояния света.
        private static readonly string NETWORK_LIGHTS_ARG = "Lights: ";
        // Аргумент метода для исполнения.
        private static readonly string NETWORK_METHOD_TO_INVOKE_ARG = "Method: ";
        // Аргумент состояния работы устройства.
        private static readonly string NETWORK_STATUS_ARG = "Status: ";
        // Метод для переключения состояния света.
        private static readonly string NETWORK_LIGHT_SWITCHER_METHOD_TO_SWITCH = "SWITCH";
        // Метод для отключения устройства.
        private static readonly string NETWORK_METHOD_TO_DISCONNECT = "DISCONNECT";
        // Метод для запроса состояния работы устройства.
        private static readonly string NETWORK_METHOD_TO_REQUEST_STATUS = "REQUEST_STATUS";
        // Состояние нормально функционирующего устройства.
        private static readonly int DEVICE_STATUS_UP = 42;
        // Подробный уровень логгирования.
        private bool _VerboseLogging;
        // Автоматическая прокрутка журнала.
        private bool _ShouldScrollToEnd;

        // Сокет программы.
        private TcpClient _Socket;
        // Поток, принимающий и обрабатывающий данные от сервера.
        private Thread _ListenerThread;
        // Мьютекс для синхронизации
        private Mutex _DataMutex;
        // Кэш принятых данных.
        private List<string> _Cache;
        // IP-адрес и порт сервера.
        private IPAddress _IPAddress;
        private int _Port;
        // Состояние света.
        private bool _Lights;

        public MainWindow()
        {
            InitializeComponent();
            // Инициализация и настройка программы.
            Init();
            Configure();
        }
        // Инициализация объектов.
        private void Init()
        {
            _DataMutex = new Mutex();

            _Cache = new List<string>();
        }
        // Настройка объектов.
        private void Configure()
        {
            // По умолчанию свет выключен.
            _Lights = false;
            // Обновить интерфейс.
            UpdateLightsStatus();
            // По умолчанию нерасширенное логгирование.
            _VerboseLogging = false;
            VerobseLoggingCheckBox.IsChecked = _VerboseLogging;
            VerobseLoggingCheckBox.Checked += (sender, e) =>
            {
                _VerboseLogging = true;
            };
            VerobseLoggingCheckBox.Unchecked += (sender, e) =>
            {
                _VerboseLogging = false;
            };
            // Автоматически прокручивать журнал.
            _ShouldScrollToEnd = true;
            ScrollToEndCheckBox.IsChecked = _ShouldScrollToEnd;
            ScrollToEndCheckBox.Checked += (sender, e) =>
            {
                _ShouldScrollToEnd = true;
            };
            ScrollToEndCheckBox.Unchecked += (sender, e) =>
            {
                _ShouldScrollToEnd = false;
            };
            /// App
            // Закрытие приложения.
            Closed += (sender, e) =>
            {
                Disconnect();
                _Socket = null;
            };

            /// Controls
            // Кнопка подключения.
            ConnectButton.IsEnabled = true;
            ConnectButton.Click += (sender, e) =>
            {
                Connect();
            };
            // Кнопка отключения.
            DisconnectButton.IsEnabled = false;
            DisconnectButton.Click += (sender, e) =>
            {
                Disconnect();

                /// Bad idea due to bad design.
                _Socket = new TcpClient();
            };
            // Кнопка переключения состояния света.
            SwitchButton.Click += (sender, e) =>
            {
                // Изменить состояние и обновить интерфейс.
                _Lights = !_Lights;
                UpdateLightsStatus();

                if (_Socket != null && _Socket.Connected)
                {
                    // Отправить новое состояние на сервер.
                    SendLightsStatus();
                }
            };
        }
        // Настройка потока, принимающего данные от сервера.
        private Thread ConfigureListenerThread()
        {
            return new Thread(new ThreadStart(delegate ()
            {
                try
                {
                    while (_Socket != null && _Socket.Connected)
                    {
                        // Принять данные.
                        byte[] bytes = new byte[BUFFER_SIZE];
                        Receive(ref _Socket, ref bytes);
                        // Закэшировать и обработать данные.
                        ProcessData(CacheData(Encoding.Unicode.GetString(bytes), ref _Cache));
                        ProcessData(ref _Cache);
                    }
                }
                catch (ThreadAbortException)
                {
                    Log(NETWORK_LOG_LABEL + "Disconnected." + '\n');
                    if (_VerboseLogging)
                    {
                        Log(NETWORK_LOG_LABEL + "Listener thread was terminated" + '\n');
                    }
                }
            }));
        }

        // Настройка потока, осуществляющего подключение.
        private Thread ConfigureConnectThread()
        {
            return new Thread(new ThreadStart(delegate ()
            {
                // Обновить интерфейс на ожидание подключения.
                Dispatcher.Invoke(delegate ()
                {
                    ConnectionStateLabel.Content = CONNECTION_WAIT;
                    SwitchButtonsOnConnectionStatusChanged(true);
                });
                Log((CONNECTION_LOG_LABEL +
                    string.Format("Connecting to {0}:{1}\n", _IPAddress.ToString(), _Port)));

                try
                {
                    // Попытка подключения к указанному адресу и порту.
                    _Socket = new TcpClient();
                    _Socket.Connect(_IPAddress, _Port);

                    Dispatcher.Invoke(delegate ()
                    {
                        ConnectionStateLabel.Content = CONNECTION_UP;
                    });
                    Log(CONNECTION_LOG_LABEL +
                        string.Format("Connected to {0}:{1}\n", _IPAddress.ToString(), _Port));
                    // Отправить информацию о себе.
                    SendInfo();
                    // Отправить состояние света.
                    SendLightsStatus();
                    // Запустить поток, получающий и обрабатывающий данные от сервера.
                    _ListenerThread = ConfigureListenerThread();
                    _ListenerThread.Start();
                }
                catch (SocketException exc)
                {
                    // Ошибка при подключении.
                    Dispatcher.Invoke(delegate ()
                    {
                        ConnectionStateLabel.Content = CONNECTION_ERR;
                        SwitchButtonsOnConnectionStatusChanged(false);
                    });
                    if (_VerboseLogging)
                    {
                        Log(CONNECTION_LOG_LABEL + exc.Message + '\n');
                    }
                }
                catch (ObjectDisposedException exc)
                {
                    Dispatcher.Invoke(delegate ()
                    {
                        ConnectionStateLabel.Content = CONNECTION_DOWN;
                        SwitchButtonsOnConnectionStatusChanged(false);
                    });
                    if (_VerboseLogging)
                    {
                        Log(CONNECTION_LOG_LABEL + exc.Message + '\n');
                    }
                }
            }));
        }

        // Подключение к серверу.
        private void Connect()
        {
            // Прочитать IP-адрес.
            try
            {
                _IPAddress = IPAddress.Parse(AddressTextBox.Text);
            }
            catch (Exception exc)
            {
                Log(IPADDRESS_LOG_LABEL + exc.Message + '\n');
                return;
            }
            // Прочитать порт.
            try
            {
                _Port = int.Parse(PortTextBox.Text);

                if (_Port < MINIMAL_PORT_VALUE || _Port > MAXIMAL_PORT_VALUE)
                {
                    throw new Exception(string.Format("Incorrect port value. [{0}; {1}] ports are allowed.",
                        MINIMAL_PORT_VALUE, MAXIMAL_PORT_VALUE));
                }
            }
            catch (Exception exc)
            {
                Log(PORT_LOG_LABEL + exc.Message + '\n');
                return;
            }
            // Запустить поток, осуществляющий подключение.
            Thread connectThread = ConfigureConnectThread();
            connectThread.Start();
        }
        // Отключение от сервера.
        private void Disconnect()
        {
            // Отправить метод для отключения.
            SendMethodToInvoke(NETWORK_METHOD_TO_DISCONNECT);
            // Завершить поток, обрабатывающий данные от сервера.
            if (_ListenerThread.IsAlive)
            {
                _ListenerThread.Abort();
            }
            // Закрыть сокет.
            if (_Socket != null)
            {
                if (_Socket.Connected)
                {
                    _Socket.Close();
                }
                else
                {
                    _Socket.Dispose();
                }
            }
            // Настроить интерфейс.
            SwitchButtonsOnConnectionStatusChanged(false);
            if (_VerboseLogging)
            {
                Log(CONNECTION_LOG_LABEL + "Connection was manually closed" + '\n');
            }
        }

        // Настроить кнопки интерфейса в зависимости от статуса подключения.
        private void SwitchButtonsOnConnectionStatusChanged(bool isConnected)
        {
            Dispatcher.Invoke(delegate ()
            {
                PortTextBox.IsEnabled = !isConnected;

                ConnectButton.IsEnabled = !isConnected;
                DisconnectButton.IsEnabled = isConnected;
            });
        }
        // Отправить данные.
        private void Send(byte[] bytes)
        {
            if (_Socket == null)
            {
                return;
            }

            try
            {
                NetworkStream stream = _Socket.GetStream();
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (System.IO.IOException exc)
            {
                SwitchButtonsOnConnectionStatusChanged(false);
                if (_VerboseLogging)
                {
                    Log(NETWORK_LOG_LABEL +
                        (exc.InnerException != null ? exc.InnerException.Message : exc.Message) + '\n');
                }
                else
                {
                    Log(CONNECTION_LOG_LABEL + "Connection's unavailable." + '\n');
                }
            }
        }
        // Принять данные.
        private void Receive(ref TcpClient socket, ref byte[] bytes)
        {
            if (_Socket == null)
            {
                return;
            }

            try
            {
                NetworkStream stream = socket.GetStream();
                stream.Read(bytes, 0, socket.ReceiveBufferSize);
            }
            catch (System.IO.IOException exc)
            {
                SwitchButtonsOnConnectionStatusChanged(false);
                if (_VerboseLogging)
                {
                    Log(NETWORK_LOG_LABEL +
                        (exc.InnerException != null ? exc.InnerException.Message : exc.Message) + '\n');
                }
                else
                {
                    Log(CONNECTION_LOG_LABEL + "Connection's unavailable." + '\n');
                }
            }
        }
        // Отправить информацию об устройстве.
        private void SendInfo()
        {
            byte[] bytes = Encoding.Unicode.GetBytes(NETWORK_DEVICE_ARG + "LightSwitcher" + DELIMITER);
            Send(bytes);

            Log(NETWORK_LOG_LABEL + "Sent info" + '\n');
        }
        // Отправить состояние света.
        private void SendLightsStatus()
        {
            byte[] bytes = Encoding.Unicode.GetBytes(string.Format(NETWORK_LIGHTS_ARG + "{0}" + DELIMITER, _Lights));
            Send(bytes);

            if (_VerboseLogging)
            {
                Log(NETWORK_LOG_LABEL + string.Format("Sent lights status: {0}", _Lights) + '\n');
            }
        }
        // Отправить состояние работы устройства.
        private void SendStatus()
        {
            byte[] bytes = Encoding.Unicode.GetBytes(string.Format(NETWORK_STATUS_ARG + "{0}" + DELIMITER, DEVICE_STATUS_UP));
            Send(bytes);

            if (_VerboseLogging)
            {
                Log(NETWORK_LOG_LABEL + string.Format("Sent status: {0}", DEVICE_STATUS_UP) + '\n');
            }
        }
        // Отправить метод для выполнения.
        private void SendMethodToInvoke(string method)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(NETWORK_METHOD_TO_INVOKE_ARG + method + DELIMITER);
            Send(bytes);

            if (_VerboseLogging)
            {
                Log(NETWORK_LOG_LABEL + "Sent method: " + method + '\n');
            }
        }
        // Закэшировать данные (подробно описано у сервера).
        string CacheData(string data, ref List<string> cache)
        {
            int delimiterIdx = data.IndexOf(DELIMITER);
            string first = data.Substring(0, delimiterIdx + 1);

            data = data.Substring(delimiterIdx + 1, data.Length - delimiterIdx - 1);
            for (delimiterIdx = data.IndexOf(DELIMITER); delimiterIdx >= 0; delimiterIdx = data.IndexOf(DELIMITER))
            {
                cache.Add(data.Substring(0, delimiterIdx + 1));
                data = data.Substring(delimiterIdx + 1, data.Length - delimiterIdx - 1);
            }

            return first;
        }

        // Обработать элемент данных.
        private void ProcessData(string data)
        {
            if (string.IsNullOrEmpty(data) || data.Equals(""))
            {
                return;
            }

            int idx;
            // Метод для исполнения.
            if ((idx = data.IndexOf(NETWORK_METHOD_TO_INVOKE_ARG)) >= 0)
            {
                int startIdx = idx + NETWORK_METHOD_TO_INVOKE_ARG.Length, endIdx = data.IndexOf(DELIMITER);
                string method = data.Substring(startIdx, endIdx - startIdx);
                // Переключить состояние света.
                if (!string.IsNullOrEmpty(method) && method.Equals(NETWORK_LIGHT_SWITCHER_METHOD_TO_SWITCH))
                {
                    Log(NETWORK_LOG_LABEL + "Lights switch was requested." + '\n');

                    _Lights = !_Lights;
                    UpdateLightsStatus();

                    SendLightsStatus();
                }
                // Запрос состояние работы устройства.
                else if (!string.IsNullOrEmpty(method) && method.Equals(NETWORK_METHOD_TO_REQUEST_STATUS))
                {
                    if (_VerboseLogging)
                    {
                        Log(NETWORK_LOG_LABEL + "Status was requested." + '\n');
                    }

                    SendStatus();
                }
            }
            else
            {
                Log(string.Format(NETWORK_LOG_LABEL + "Received unknown data: \"{0}\"" + '\n', data));
            }
        }
        // Обработать список элементов данных.
        private void ProcessData(ref List<string> dataSet)
        {
            _DataMutex.WaitOne();

            foreach (string data in dataSet)
            {
                ProcessData(data);
            }

            dataSet.Clear();

            _DataMutex.ReleaseMutex();
        }
        // Обновить интерфейс в зависимости от состояние света.
        private void UpdateLightsStatus()
        {
            Dispatcher.Invoke(delegate ()
            {
                LightsValueLabel.Content = _Lights ? "on" : "off";
            });
        }

        // Добавить запись в журнал.
        private void Log(string info)
        {
            try
            {
                Dispatcher.Invoke(delegate ()
                {
                    LogTextBlock.AppendText(info);
                    if (_ShouldScrollToEnd)
                    {
                        LogTextBlock.ScrollToEnd();
                    }
                });
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }
}
