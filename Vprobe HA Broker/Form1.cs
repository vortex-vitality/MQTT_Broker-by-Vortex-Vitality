using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using MQTTnet.Protocol;
using MQTTnet.Server;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MQTTnet.Client.Subscribing;
using MQTTnet.Client.Unsubscribing;

// WPF MessageBox types used throughout the existing UI logic.
using System.Windows;
using MessageBox = System.Windows.MessageBox;

// M2Mqtt local client
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using M2MqttClient = uPLibrary.Networking.M2Mqtt.MqttClient;

namespace Vprobe_HA_Broker
{
    public partial class Form1 : Form
    {
        // Auth/session
        private string _jwt = string.Empty;
        private string _uuid = string.Empty;

        // AWS IoT (JWT over WebSockets) client (MQTTnet)
        private IMqttClient _awsWsClient;
        private bool _awsFirstMsgLogged;
        private readonly SemaphoreSlim _awsWsConnectLock = new SemaphoreSlim(1, 1);

        private static readonly string AwsIotEndpointHost = ConfigurationManager.AppSettings["AWS_IOT_ENDPOINT_HOST"];
        private static readonly string AwsCustomAuthorizerName = ConfigurationManager.AppSettings["AWS_AUTHORIZER"];

        // UI data
        private readonly List<string> products = new List<string>();
        private readonly List<string> haTopics = new List<string>();
        private readonly List<string> selected = new List<string>();
        private readonly List<string> rejected = new List<string>();

        // Local broker + client
        private string[] ipaddress1, ipaddress2;
        private IMqttServer mqttServer;
        private M2MqttClient AppClient1;
        private bool AppClient1Connected;
        private bool AppClient1Disconnected;
        private int serverRunTime;
        private int reconnectAttempts;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public Form1()
        {
            InitializeComponent();
        }

        // ---------------------------
        // Logging
        // ---------------------------
        private static readonly object _logLock = new object();

        private void Log(string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("o") + " " + msg;
                System.Diagnostics.Debug.WriteLine(line);

                lock (_logLock)
                {
                    File.AppendAllText(
                        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "broker.log"),
                        line + Environment.NewLine);
                }
            }
            catch { }
        }

        // ---------------------------
        // JWT cache (CurrentUser)
        // ---------------------------
        private static string JwtCachePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Vprobe_HA_Broker");

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "jwt.bin");
        }

        private void SaveJwtToDiskCurrentUser(string uuid, string jwt)
        {
            if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(jwt))
                return;

            var payload = $"{uuid}\n{jwt}";
            var bytes = Encoding.UTF8.GetBytes(payload);

            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(JwtCachePath(), protectedBytes);
        }

        private bool TryLoadJwtFromDiskCurrentUser(out string uuid, out string jwt)
        {
            uuid = null;
            jwt = null;

            try
            {
                var path = JwtCachePath();
                if (!File.Exists(path)) return false;

                var protectedBytes = File.ReadAllBytes(path);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);

                var payload = Encoding.UTF8.GetString(bytes);
                var parts = payload.Split(new[] { '\n' }, 2);
                if (parts.Length != 2) return false;

                uuid = parts[0].Trim();
                jwt = parts[1].Trim();
                return !string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(jwt);
            }
            catch
            {
                return false;
            }
        }
        // ---------------------------
        
        public static bool IsInternetAvailable()
        {
            using (var client = new WebClient())
            {
                try
                {
                    using (client.OpenRead("https://aws.amazon.com/")) { }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool CheckNet()
        {
            if (IsInternetAvailable()) return true;

            MessageBox.Show(
                "No Access To Internet! Operation Aborted.",
                "Internet Connection Problem",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return false;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            string appNameAndVersion =
                Assembly.GetExecutingAssembly().GetName().Name +
                " v" +
                Assembly.GetExecutingAssembly().GetName().Version.Major + "." +
                Assembly.GetExecutingAssembly().GetName().Version.Minor + "." +
                Assembly.GetExecutingAssembly().GetName().Version.Build;

            this.Text = appNameAndVersion;

            switch (Assembly.GetExecutingAssembly().GetName().Version.Revision)
            {
                case 1:
                    this.Text = appNameAndVersion + " - alpha";
                    break;
                case 2:
                    this.Text = appNameAndVersion + " - beta";
                    break;
            }

            panel1.Location = new Point(105, panel1.Location.Y);

            // Optional: restore last JWT/uuid for smoother reconnects
            if (TryLoadJwtFromDiskCurrentUser(out var cachedUuid, out var cachedJwt))
            {
                _uuid = cachedUuid;
                _jwt = cachedJwt;
            }
        }

        // ---------------------------
        // Lambda calls
        // ---------------------------
        public async Task<string> GetLambdaResponse(string myData)
        {
            string baseUrl = ConfigurationManager.AppSettings["VP_HA_FUNCTION_URL"];

            dynamic payload = JsonConvert.DeserializeObject<dynamic>(myData);
            string requestType = (string)payload.requestType;
            string authCode = (string)payload.authCode;

            // GET for list of devices
            if (requestType == "loggingIn" || requestType == "refresh")
            {
                string url =
                    $"{baseUrl.TrimEnd('/')}/" +
                    $"?requestType={Uri.EscapeDataString(requestType)}" +
                    $"&authCode={Uri.EscapeDataString(authCode)}";

                using (var resp = await Http.GetAsync(url))
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception($"HTTP {(int)resp.StatusCode}: {body}");
                    return body;
                }
            }

            // POST for linking devices
            string postUrl = $"{baseUrl.TrimEnd('/')}/";
            using (var content = new StringContent(myData, Encoding.UTF8, "application/json"))
            using (var resp = await Http.PostAsync(postUrl, content))
            {
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"HTTP {(int)resp.StatusCode}: {body}");
                return body;
            }
        }

        // ---------------------------
        // Login
        // ---------------------------
        private async void button1_Click(object sender, EventArgs e)
        {
            if (textBox1.Text.Length < 6)
            {
                MessageBox.Show(
                    "Incorrect Auth Code. The Auth Code must be the 6-number long code generated by Vplants.",
                    "ERROR",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return;
            }

            if (!CheckNet()) return;

            progressBar1.Visible = true;
            button1.Enabled = false;

            dynamic payload = new ExpandoObject();
            payload.requestType = "loggingIn";
            payload.authCode = textBox1.Text;

            try
            {
                string response = await GetLambdaResponse(JsonConvert.SerializeObject(payload));
                dynamic rsp = JsonConvert.DeserializeObject<dynamic>(response);

                switch ((string)rsp.status)
                {
                    case "SUCCESS":
                        {
                            string jwt = (string)rsp.jwt;
                            string uuid = (string)rsp.uuid;
                            string haTopicBase = (string)rsp.haTopic;

                            // Fallback: if uuid not provided, derive from haTopic = "vv/ha/sensor/<uuid>"
                            if (string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(haTopicBase))
                            {
                                var seg = haTopicBase.Split('/');
                                if (seg.Length >= 4) uuid = seg[3];
                            }

                            // Store
                            if (!string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(jwt))
                            {
                                _uuid = uuid;
                                _jwt = jwt;
                                SaveJwtToDiskCurrentUser(_uuid, _jwt);
                            }

                            // IMPORTANT: if haTopic is empty on login, fallback to the canonical base
                            if (string.IsNullOrWhiteSpace(haTopicBase) && !string.IsNullOrWhiteSpace(_uuid))
                                haTopicBase = $"vv/ha/sensor/{_uuid}";

                            Log($"LOGIN SUCCESS: uuid='{_uuid}', haTopic='{haTopicBase}', jwt_len={(_jwt?.Length ?? 0)}");

                            PopulateProductsFromResponse(rsp);

                            panel2.Location = new Point(100, panel1.Location.Y);
                            panel2.Size = new Size(300, panel2.Height);
                            listBox1.Size = new Size(290, listBox1.Height);

                            panel1.Visible = false;
                            panel2.Visible = true;
                            panel3.Visible = true;

                            label8.Text = "Linked products: " + selected.Count;
                            if (selected.Count > 0)
                            {
                                panel2.Enabled = false;
                                button2.Text = "Edit Linked Products";
                            }
                            else
                            {
                                panel2.Enabled = true;
                                button2.Text = "Link Selected with Home Assistant";
                            }

                            // Start local broker if needed
                            if (mqttServer == null || !mqttServer.IsStarted)
                            {
                                getIPaddress();
                                startServer();
                            }

                            // Connect AWS + subscribe to selected SN topics (so HA gets states immediately)
                            if (_awsWsClient == null || !_awsWsClient.IsConnected)
                            {
                                if (!await MQTTConnectClient())
                                {
                                    MessageBox.Show("MQTT Client failed to connect to the Cloud.", "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
                                    label8.Text = "Error. Connection Failed!";
                                    return;
                                }
                            }

                            haTopics.Clear();
                            foreach (string sn in selected)
                            {
                                string t = $"{haTopicBase}/{sn}";
                                haTopics.Add(t);
                                SubscribeToHaTopicAsync(t);
                            }

                            break;
                        }

                    case "TIMEOUT":
                        MessageBox.Show("Auth Code expired. Try again", "TIMEOUT", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;

                    case "ERROR":
                        MessageBox.Show("Incorrect Auth Code. Try again", "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                }
            }
            catch (Exception ex)
            {
                Log("LOGIN ERROR: " + ex.Message);
                MessageBox.Show("Unexpected error during login.\n\n" + ex.Message, "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                progressBar1.Visible = false;
                button1.Enabled = true;
            }
        }

        private void PopulateProductsFromResponse(dynamic rsp)
        {
            // products
            var array = rsp.products;
            IList collection = (IList)array;

            products.Clear();
            listBox1.Items.Clear();

            for (int i = 0; i < collection.Count; i++)
            {
                string sn = collection[i].ToString();
                products.Add(sn);
                listBox1.Items.Add("Vprobe-SN" + string.Format("{0:00000000}", uint.Parse(sn)));
            }

            // selected
            selected.Clear();
            var selectedArray = rsp.selected;
            IList collection2 = (IList)selectedArray;
            for (int i = 0; i < collection2.Count; i++)
            {
                string selectedSN = collection2[i].ToString();
                selected.Add(selectedSN);
                listBox1.SetSelected(products.IndexOf(selectedSN), true);
            }
        }

        // ---------------------------
        // IP helpers
        // ---------------------------
        public static string[] GetAllLocalIPv4(NetworkInterfaceType _type)
        {
            var ipAddrList = new List<string>();
            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (item.NetworkInterfaceType == _type && item.OperationalStatus == OperationalStatus.Up)
                {
                    foreach (UnicastIPAddressInformation ip in item.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                            ipAddrList.Add(ip.Address.ToString());
                    }
                }
            }
            return ipAddrList.ToArray();
        }

        private string getIPaddress()
        {
            ipaddress1 = GetAllLocalIPv4(NetworkInterfaceType.Wireless80211);
            ipaddress2 = GetAllLocalIPv4(NetworkInterfaceType.Ethernet);

            comboBox1.Items.Clear();

            foreach (var ip in ipaddress1) comboBox1.Items.Add(ip);
            foreach (var ip in ipaddress2) comboBox1.Items.Add(ip);

            if (ipaddress1.Count() == 1)
            {
                comboBox1.Text = ipaddress1[0];
            }
            else if (ipaddress2.Count() >= 1)
            {
                if (ipaddress2.Count() > 1)
                {
                    MessageBox.Show(
                        "More than one local IPv4 is found on this machine: " + string.Join(", ", ipaddress2) +
                        "\n\nThe first one will be used. If it does not work with Home Assistant, select another IP from the drop-down list or enter it manually.",
                        "Broker IP address",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                comboBox1.Text = ipaddress2[0];
            }
            else if (ipaddress1.Count() > 1)
            {
                MessageBox.Show(
                    "More than one local IPv4 is found on this machine: " + string.Join(", ", ipaddress1) +
                    "\n\nThe first one will be used. If it does not work with Home Assistant, select another IP from the drop-down list or enter it manually.",
                    "Broker IP address",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                comboBox1.Text = ipaddress1[0];
            }
            else
            {
                MessageBox.Show(
                    "No local IPv4 is detected on this machine.\n\nEnsure your local adapter is enabled and the local network works correctly, or enter the IPv4 manually.",
                    "No IP address found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                comboBox1.Text = "enter manually";
            }

            comboBox1.Refresh();
            Thread.Sleep(100);
            return comboBox1.Text;
        }

        public bool ValidateIPv4(string ipString)
        {
            if (string.IsNullOrWhiteSpace(ipString)) return false;

            string[] splitValues = ipString.Split('.');
            if (splitValues.Length != 4) return false;

            return splitValues.All(r => byte.TryParse(r, out _));
        }

        // ---------------------------
        // Local broker + client
        // ---------------------------
        private bool startServer()
        {
            button2.Enabled = false;
            button5.Enabled = false;
            label9.Visible = false;
            timer1.Enabled = false;

            label9.Text = "Run Time: " + TimeSpan.FromSeconds(0).ToString(@"hh\:mm\:ss");

            string ipAddress = comboBox1.Text;
            if (!ValidateIPv4(ipAddress))
            {
                label2.Text = "Broker failed, check IP address..";
                label2.ForeColor = Color.Red;
                comboBox1.ForeColor = Color.Red;

                MessageBox.Show("Enter the correct IPv4 address.", "Incorrect IP format", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var options = new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(1883)
                .WithDefaultEndpointBoundIPAddress(IPAddress.Parse(ipAddress));

            mqttServer = new MqttFactory().CreateMqttServer();

            try
            {
                mqttServer.StartAsync(options.Build()).ConfigureAwait(false).GetAwaiter().GetResult();

                timer3.Enabled = false;
                if (!connectClient(ipAddress))
                {
                    label2.Text = "Client disconnected..";
                    label2.ForeColor = Color.Red;

                    timer3.Interval = 2000;
                    reconnectAttempts = 8;
                    timer3.Enabled = true;
                    return false;
                }

                label2.Text = "Broker started and running OK..";
                label2.ForeColor = Color.Green;
                comboBox1.ForeColor = Color.Green;

                button2.Enabled = true;
                button5.Enabled = true;

                label9.Visible = true;
                serverRunTime = 0;
                timer1.Enabled = true;

                return true;
            }
            catch
            {
                label2.Text = "Broker failed, check IP address..";
                label2.ForeColor = Color.Red;
                comboBox1.ForeColor = Color.Red;

                MessageBox.Show("Enter the correct IPv4 address.", "IP Address Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
        }

        private async void stopServer()
        {
            try
            {
                if (mqttServer != null)
                {
                    await mqttServer.StopAsync();
                    mqttServer.Dispose();
                    mqttServer = null;

                    button2.Enabled = false;
                    button5.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                Log("stopServer ERROR: " + ex.Message);
            }
        }

        private bool connectClient(string ip)
        {
            string clientId = "VprobeHA";

            AppClient1 = new M2MqttClient(ip);
            AppClient1.ConnectionClosed += AppClient1_Disconnected;
            AppClient1.MqttMsgPublishReceived += AppClient1_ApplicationMessageReceived;

            for (int i = 0; i < 2; i++)
            {
                try
                {
                    AppClient1Disconnected = false;
                    AppClient1.Connect(clientId, "", "");
                    AppClient1Connected = AppClient1.IsConnected;
                }
                catch
                {
                    AppClient1Connected = false;
                }

                if (AppClient1Connected) break;
                Thread.Sleep(500);
            }

            if (!AppClient1Connected) return false;

            AppClient1.Subscribe(
                new[] { "homeassistant/status" },
                new[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });

            return true;
        }

        public void AppClient1_Disconnected(object sender, EventArgs e)
        {
            AppClient1Disconnected = true;
            AppClient1Connected = false;

            reconnectAttempts = 8;

            try
            {
                label2.Invoke((MethodInvoker)(() => label2.Text = "Client disconnected.."));
                label2.Invoke((MethodInvoker)(() => label2.ForeColor = Color.Red));
                timer3.Interval = 2000;
                timer3.Enabled = true;
            }
            catch { }
        }

        private bool EnsureLocalMqttConnected()
        {
            if (AppClient1 != null && AppClient1.IsConnected) return true;

            var ip = comboBox1.Text;
            Log($"Local MQTT client disconnected; trying reconnect to {ip}...");
            return connectClient(ip);
        }

        // ---------------------------
        // Home Assistant discovery
        // ---------------------------
        private void publishConfigMsgs()
        {
            foreach (string sn in selected)
            {
                dynamic dev = new ExpandoObject();
                dev.identifiers = "VprobeSN" + sn;
                dev.name = "Vprobe SN" + sn;
                dev.manufacturer = "Vortex Vitality";
                dev.model = "Model 1";
                dev.serial_number = "Vprobe-SN" + string.Format("{0:00000000}", int.Parse(sn));

                PublishSensorConfig(sn, "Light", "light", "Lux", "{{ value_json.light }}", "mdi:lightbulb-on-outline", dev);
                PublishSensorConfig(sn, "Temperature", "temperature", "°C", "{{ value_json.temp }}", "mdi:thermometer", dev, uniqueSuffixOverride: "_temerature");
                PublishSensorConfig(sn, "Humidity", "humidity", "%", "{{ value_json.humidity }}", "mdi:water-percent", dev);
                PublishSensorConfig(sn, "Soil Moisture", "moisture", "%", "{{ value_json.moisture }}", "mdi:watering-can-outline", dev);

                // SN < 1000 devices don't support Fertility + Soil Temperature
                if (int.TryParse(sn, out var snInt) && snInt >= 1000)
                {
                    PublishSensorConfig(sn, "Fertility", "fertility", "µS/cm", "{{ value_json.fertility }}", "mdi:leaf", dev);
                    PublishSensorConfig(sn, "Soil Temperature", "soilTemp", "°C", "{{ value_json.soilTemp }}", "mdi:thermometer", dev);
                }
                else
                {
                    // If these were published before, remove them (retained discovery)
                    PublishNull($"homeassistant/sensor/VprobeSN{sn}fertility/config");
                    PublishNull($"homeassistant/sensor/VprobeSN{sn}soilTemp/config");
                }

                PublishSensorConfig(sn, "Battery", "battery", "V", "{{ value_json.batt }}", "mdi:battery-outline", dev);
                PublishSensorConfig(sn, "Epoch", "time", null, "{{ value_json.time }}", "mdi:clock-time-eight-outline", dev);
            }

            foreach (string sn in rejected)
            {
                PublishNull($"homeassistant/sensor/VprobeSN{sn}light/config");
                PublishNull($"homeassistant/sensor/VprobeSN{sn}temperature/config");
                PublishNull($"homeassistant/sensor/VprobeSN{sn}humidity/config");
                PublishNull($"homeassistant/sensor/VprobeSN{sn}moisture/config");
                PublishNull($"homeassistant/sensor/VprobeSN{sn}fertility/config");
                PublishNull($"homeassistant/sensor/VprobeSN{sn}soilTemp/config");
                PublishNull($"homeassistant/sensor/VprobeSN{sn}battery/config");
                PublishNull($"homeassistant/sensor/VprobeSN{sn}time/config");
            }
        }

        private void PublishNull(string topic)
        {
            AppClient1.Publish(topic, null, 1, true);
        }

        private void PublishSensorConfig(
            string sn,
            string name,
            string objectIdSuffix,
            string unit,
            string valueTemplate,
            string icon,
            dynamic device,
            string uniqueSuffixOverride = null)
        {
            dynamic payload = new ExpandoObject();
            payload.name = name;

            string uniqueSuffix = uniqueSuffixOverride ?? "_" + objectIdSuffix;
            payload.unique_id = "VprobeSN" + sn + uniqueSuffix;

            payload.state_topic = "VprobeSN" + sn + "/sensor/state";
            if (!string.IsNullOrWhiteSpace(unit)) payload.unit_of_measurement = unit;
            payload.value_template = valueTemplate;
            payload.device = device;
            if (!string.IsNullOrWhiteSpace(icon)) payload.icon = icon;

            string json = JsonConvert.SerializeObject(payload);
            AppClient1.Publish($"homeassistant/sensor/VprobeSN{sn}{objectIdSuffix}/config", Encoding.UTF8.GetBytes(json), 0, true);
        }

        // ---------------------------
        // AWS IoT WebSockets (JWT)
        // ---------------------------
        public async Task<bool> MQTTConnectClient()
        {
            if (!CheckNet()) return false;
            if (string.IsNullOrWhiteSpace(_uuid) || string.IsNullOrWhiteSpace(_jwt)) return false;

            await _awsWsConnectLock.WaitAsync();
            try
            {
                if (_awsWsClient != null && _awsWsClient.IsConnected) return true;

                string wsServer =
                    $"{AwsIotEndpointHost}:443/mqtt?x-amz-customauthorizer-name={Uri.EscapeDataString(AwsCustomAuthorizerName)}";

                var factory = new MqttFactory();

                if (_awsWsClient == null)
                {
                    _awsWsClient = factory.CreateMqttClient();

                    _awsWsClient.UseApplicationMessageReceivedHandler(e =>
                    {
                        var t = e?.ApplicationMessage?.Topic;
                        var p = e?.ApplicationMessage?.Payload;

                        if (!_awsFirstMsgLogged)
                        {
                            Log($"AWS RX handler: topic='{t}', bytes={(p?.Length ?? 0)}");
                            _awsFirstMsgLogged = true;
                        }

                        HandleAwsIotMessage(t, p);
                    });

                    _awsWsClient.UseDisconnectedHandler(async e =>
                    {
                        Log($"AWS DISCONNECTED: reason='{e.Reason}', ex='{e.Exception?.Message}'");
                        await Task.Delay(2000);

                        try
                        {
                            if (CheckNet() && await MQTTConnectClient())
                            {
                                await SubscribeToHaTopicAsync($"vv/ha/sensor/{_uuid}/#");
                            }
                        }
                        catch { }
                    });
                }

                var options = new MqttClientOptionsBuilder()
                    .WithClientId(_uuid)
                    .WithCredentials(_uuid, _jwt)
                    .WithWebSocketServer(wsServer)
                    .WithTls(new MqttClientOptionsBuilderTlsParameters
                    {
                        UseTls = true,
                        SslProtocol = SslProtocols.Tls12
                    })
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                    .WithCommunicationTimeout(TimeSpan.FromSeconds(12))
                    .Build();

                if (options.ChannelOptions is MqttClientWebSocketOptions wsOptions)
                {
                    wsOptions.SubProtocols = new List<string> { "mqtt" };
                }

                Log($"AWS CONNECT start: clientId='{_uuid}', jwt_len={(_jwt?.Length ?? 0)}, wsServer='{wsServer}'");
                await _awsWsClient.ConnectAsync(options);
                Log($"AWS CONNECT done: connected={_awsWsClient.IsConnected}, clientId='{_uuid}'");

                if (_awsWsClient.IsConnected)
                {
                    await SubscribeToHaTopicAsync($"vv/ha/sensor/{_uuid}/#");
                }

                return _awsWsClient.IsConnected;
            }
            finally
            {
                _awsWsConnectLock.Release();
            }
        }

        private async Task SubscribeToHaTopicAsync(string topic)
        {
            if (_awsWsClient == null || !_awsWsClient.IsConnected) return;

            var subOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(topic)
                .Build();

            var res = await _awsWsClient.SubscribeAsync(subOptions);

            if (res?.Items != null && res.Items.Count > 0)
                Log($"SUBACK {topic} => {res.Items[0].ResultCode}");
            else
                Log($"SUBACK {topic} => (no result)");
        }

        private async Task UnsubscribeFromHaTopicAsync(string topic)
        {
            if (_awsWsClient == null || !_awsWsClient.IsConnected) return;
            await _awsWsClient.UnsubscribeAsync(topic);
        }

        // ---------------------------
        // AWS message -> HA state publish
        // ---------------------------

        private void HandleAwsIotMessage(string topic, byte[] payloadBytes)
        {
            if (string.IsNullOrWhiteSpace(topic) || payloadBytes == null || payloadBytes.Length == 0) return;

            var parts = topic.Split('/');
            if (parts.Length < 5) return;
            var sn = parts[4];

            JObject jo;
            try
            {
                jo = JObject.Parse(Encoding.UTF8.GetString(payloadBytes));
            }
            catch { return; }

            // Optional SN check
            var snPayload = jo["SN"]?.ToString();
            if (!string.IsNullOrWhiteSpace(snPayload) && snPayload != sn) return;

            if (!EnsureLocalMqttConnected())
            {
                Log("ERROR: Local MQTT not connected -> cannot publish state to HA.");
                return;
            }

            // parse SN as int from topic
            int.TryParse(sn, out var snInt);

            // soilTemp can arrive as soilTemp (preferred) or soiltemp (legacy)
            var soilT = jo["soilTemp"]?.Value<double?>() ?? jo["soiltemp"]?.Value<double?>();

            var packed = jo["time"]?.Value<int?>();
            int? epoch = packed.HasValue ? TS_to_Epoch(packed.Value) : (int?)null;

            var state = new JObject
            {
                ["light"] = jo["light"],
                ["temp"] = jo["temp"],
                ["humidity"] = jo["humidity"],
                ["moisture"] = jo["moisture"],
                ["batt"] = jo["batt"],
                ["time"] = epoch
            };

            // Only SN >= 1000 supports fertility + soilTemp
            if (snInt >= 1000)
            {
                state["fertility"] = jo["fertility"];
                state["soilTemp"] = soilT;
            }

            // drop nulls so HA doesn't get "null" states
            state = JObject.FromObject(state.ToObject<Dictionary<string, object>>()
                .Where(kv => kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => kv.Value));

            AppClient1.Publish(
                $"VprobeSN{sn}/sensor/state",
                Encoding.UTF8.GetBytes(state.ToString(Formatting.None)),
                0,
                true
            );
        }

        private int TS_to_Epoch(int packedTimestamp)
        {
            int ts = packedTimestamp;
            int sec = (ts & 0x3F); ts >>= 6;
            int min = (ts & 0x3F); ts >>= 6;
            int hour = (ts & 0x1F); ts >>= 5;
            int day = (ts & 0x1F); ts >>= 5;
            int mon = (ts & 0x0F); ts >>= 4;
            int year = (2020 + (ts & 0x3F));

            string isoString = $"{year}-{mon:00}-{day:00}T{hour:00}:{min:00}:{sec:00}";
            var date = DateTime.Parse(isoString, null, DateTimeStyles.RoundtripKind);
            return (int)(date.ToUniversalTime().Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
        }

        // ---------------------------
        // Local client inbound (HA online)
        // ---------------------------
        private async void AppClient1_ApplicationMessageReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;
            string message = e.Message != null ? Encoding.UTF8.GetString(e.Message) : "";

            if (topic == "homeassistant/status" && message == "online")
            {
                publishConfigMsgs();

                bool okToSubscribe = (_awsWsClient != null && _awsWsClient.IsConnected) || await MQTTConnectClient();
                if (okToSubscribe)
                {
                    foreach (string haTopic in haTopics)
                        SubscribeToHaTopicAsync(haTopic);
                }
            }
        }

        // ---------------------------
        // Link / Unlink
        // ---------------------------
        private async void button2_Click(object sender, EventArgs e)
        {
            if (!CheckNet()) return;

            if (!panel2.Enabled)
            {
                panel2.Enabled = true;
                button2.Text = "Link Selected with Home Assistant";
                return;
            }

            if (listBox1.SelectedIndices.Count <= 0)
            {
                MessageBox.Show(
                    products.Count == 0
                        ? "No Vprobe products found in your Vplants account! Add at least one plant first."
                        : "No products selected!",
                    products.Count == 0 ? "NO PRODUCTS FOUND" : "SELECTED PRODUCTS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            button2.Enabled = false;

            selected.Clear();
            foreach (int i in listBox1.SelectedIndices)
                selected.Add(products[i]);

            rejected.Clear();
            foreach (string sn in products)
                if (selected.IndexOf(sn) < 0) rejected.Add(sn);

            string[] selectedProducts = selected.ToArray();
            string[] rejectedProducts = rejected.ToArray();

            dynamic payload = new ExpandoObject();
            payload.requestType = "getTopicName";
            payload.authCode = textBox1.Text;
            payload.selectedProducts = selectedProducts;
            payload.rejectedProducts = rejectedProducts;
            payload.uuid = _uuid;

            try
            {
                Log($"getTopicName REQUEST: _uuid='{_uuid}', selected={selectedProducts.Length}, rejected={rejectedProducts.Length}");
                string response = await GetLambdaResponse(JsonConvert.SerializeObject(payload));

                dynamic rsp = JsonConvert.DeserializeObject<dynamic>(response);
                switch ((string)rsp.status)
                {
                    case "SUCCESS":
                        {
                            string topicBase = (string)rsp.haTopic;
                            Log($"getTopicName SUCCESS: haTopic='{topicBase}', _uuid='{_uuid}'");

                            haTopics.Clear();
                            foreach (string sn in selectedProducts)
                            {
                                string t = $"{topicBase}/{sn}";
                                haTopics.Add(t);
                                SubscribeToHaTopicAsync(t);
                            }

                            foreach (string sn in rejectedProducts)
                                UnsubscribeFromHaTopicAsync($"{topicBase}/{sn}");

                            label8.Text = "Linked products: " + selected.Count;
                            publishConfigMsgs();

                            timer2.Enabled = true;

                            panel2.Enabled = false;
                            button2.Text = "Edit Linked Products";

                            MessageBox.Show(
                                "Now refresh the page or restart Home Assistant to see your linked products.\n\n" +
                                "Keep this Broker running at all times to ensure continuous data reception by Home Assistant.\n\n" +
                                "If no sensor states were updated after this step, check your local network settings and ensure port 1883 is reachable.",
                                "PRODUCTS LINKED",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                            break;
                        }

                    case "TIMEOUT":
                        MessageBox.Show("You were logged out. Please login again.", "TIMEOUT", MessageBoxButton.OK, MessageBoxImage.Information);
                        panel1.Visible = true;
                        panel2.Visible = false;
                        panel3.Visible = false;
                        break;

                    default:
                        MessageBox.Show("Unexpected Error. Try again.", "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("getTopicName ERROR: " + ex.Message);
                MessageBox.Show("Unexpected Error. Try again.\n\n" + ex.Message, "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                button2.Enabled = true;
            }
        }

        // ---------------------------
        // UI handlers (kept for designer wiring)
        // ---------------------------
        private void button3_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < listBox1.Items.Count; i++) listBox1.SetSelected(i, true);
        }

        private void button4_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < listBox1.Items.Count; i++) listBox1.SetSelected(i, false);
        }

        private void Form1_Activated(object sender, EventArgs e) => textBox1.Focus();

        private void backgroundWorker1_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e) { }

        private async void pictureBox2_Click(object sender, EventArgs e)
        {
            dynamic payload = new ExpandoObject();
            payload.requestType = "refresh";
            payload.authCode = textBox1.Text;

            try
            {
                string response = await GetLambdaResponse(JsonConvert.SerializeObject(payload));
                dynamic rsp = JsonConvert.DeserializeObject<dynamic>(response);

                switch ((string)rsp.status)
                {
                    case "SUCCESS":
                        {
                            string jwt = (string)rsp.jwt;
                            string uuid = (string)rsp.uuid;

                            if (!string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(jwt))
                            {
                                _uuid = uuid;
                                _jwt = jwt;
                                SaveJwtToDiskCurrentUser(_uuid, _jwt);
                            }

                            PopulateProductsFromResponse(rsp);

                            string topicBase = (string)rsp.haTopic;
                            if (string.IsNullOrWhiteSpace(topicBase) && !string.IsNullOrWhiteSpace(_uuid))
                                topicBase = $"vv/ha/sensor/{_uuid}";

                            haTopics.Clear();
                            foreach (string sn in selected)
                                haTopics.Add($"{topicBase}/{sn}");

                            label8.Text = "Linked products: " + selected.Count;
                            break;
                        }

                    default:
                        MessageBox.Show("You were logged out. Please login again.", "TIMEOUT", MessageBoxButton.OK, MessageBoxImage.Information);
                        panel1.Visible = true;
                        panel2.Visible = false;
                        panel3.Visible = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("refresh ERROR: " + ex.Message);
                MessageBox.Show("Refresh failed.\n\n" + ex.Message, "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void button5_Click(object sender, EventArgs e)
        {
            var result = MessageBox.Show(
                "This operation will remove all linked products from Home Assistant.\n\nDo you want to continue?",
                "DELETE ALL PRODUCTS",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No);

            if (result == MessageBoxResult.No) return;

            string[] selectedProducts = { };
            string[] rejectedProducts = products.ToArray();

            dynamic payload = new ExpandoObject();
            payload.requestType = "getTopicName";
            payload.authCode = textBox1.Text;
            payload.selectedProducts = selectedProducts;
            payload.rejectedProducts = rejectedProducts;
            payload.uuid = _uuid;

            try
            {
                string response = await GetLambdaResponse(JsonConvert.SerializeObject(payload));
                dynamic rsp = JsonConvert.DeserializeObject<dynamic>(response);

                switch ((string)rsp.status)
                {
                    case "SUCCESS":
                        foreach (string sn in products)
                        {
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}light/config");
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}temperature/config");
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}humidity/config");
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}moisture/config");
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}fertility/config");
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}soilTemp/config");
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}battery/config");
                            PublishNull($"homeassistant/sensor/VprobeSN{sn}time/config");
                        }

                        haTopics.Clear();
                        selected.Clear();
                        listBox1.ClearSelected();
                        label8.Text = "Linked products: " + selected.Count;
                        break;

                    case "TIMEOUT":
                        MessageBox.Show("You were logged out. Please login again.", "TIMEOUT", MessageBoxButton.OK, MessageBoxImage.Information);
                        panel1.Visible = true;
                        panel2.Visible = false;
                        panel3.Visible = false;
                        break;

                    default:
                        MessageBox.Show("Unexpected Error. Try again.", "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log("delete all ERROR: " + ex.Message);
                MessageBox.Show("Unexpected Error. Try again.\n\n" + ex.Message, "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void restartServer()
        {
            stopServer();
            Thread.Sleep(100);

            if (!startServer())
            {
                label2.ForeColor = Color.Blue;
                label2.Text = "Attempting to use default IP...";
                comboBox1.ForeColor = Color.Blue;

                getIPaddress();
                Thread.Sleep(1000);
                startServer();
            }

            pictureBox4.Focus();
        }

        private void pictureBox4_Click(object sender, EventArgs e) => restartServer();

        private void comboBox1_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (!(char.IsDigit(e.KeyChar) || e.KeyChar == '.' || e.KeyChar == '\b' || e.KeyChar == '\r'))
                e.Handled = true;

            if (e.KeyChar == '\r') restartServer();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e) => stopServer();

        private void comboBox1_MouseClick(object sender, MouseEventArgs e)
        {
            if (comboBox1.Text == "enter manually") comboBox1.Text = "192.168.0.1";
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            serverRunTime++;
            TimeSpan t = TimeSpan.FromSeconds(serverRunTime);
            label9.Text = t.Days > 0 ? "Run Time: " + t.ToString(@"d\.hh\:mm\:ss") : "Run Time: " + t.ToString(@"hh\:mm\:ss");
        }

        private async void timer2_Tick(object sender, EventArgs e)
        {
            timer2.Enabled = false;

            dynamic payload = new ExpandoObject();
            payload.requestType = "forceDataUpdate";
            payload.authCode = textBox1.Text;
            payload.selectedProducts = selected.ToArray();
            payload.rejectedProducts = rejected.ToArray();
            payload.uuid = _uuid;

            try
            {
                string response = await GetLambdaResponse(JsonConvert.SerializeObject(payload));
                dynamic rsp = JsonConvert.DeserializeObject<dynamic>(response);

                if ((string)rsp.status == "TIMEOUT")
                {
                    MessageBox.Show("You were logged out. Please login again.", "TIMEOUT", MessageBoxButton.OK, MessageBoxImage.Information);
                    panel1.Visible = true;
                    panel2.Visible = false;
                    panel3.Visible = false;
                }
                else if ((string)rsp.status == "ERROR")
                {
                    MessageBox.Show("Unexpected Error. Try again.", "ERROR", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log("forceDataUpdate ERROR: " + ex.Message);
            }
        }

        private void comboBox1_DropDownClosed(object sender, EventArgs e) => restartServer();

        private void textBox3_Click(object sender, EventArgs e) => pictureBox4.Focus();
        private void richTextBox1_Click(object sender, EventArgs e) => pictureBox4.Focus();

        private void panel1_VisibleChanged(object sender, EventArgs e)
        {
            if (panel1.Visible)
            {
                button1.Enabled = true;
                progressBar1.Visible = false;
            }
        }

        private void timer3_Tick(object sender, EventArgs e)
        {
            reconnectAttempts--;
            if (reconnectAttempts <= 0)
            {
                timer3.Enabled = false;
                return;
            }

            string ipAddress = comboBox1.Text;
            if (connectClient(ipAddress))
            {
                timer3.Enabled = false;
            }
            else
            {
                timer3.Interval *= 2;
            }
        }

        private void pictureBox3_Click(object sender, EventArgs e)
        {
            System.Diagnostics.Process.Start("https://vortexvitality.uk/vplants/home-assistant/");
        }
    }
}
