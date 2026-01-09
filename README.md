# MQTT Broker by Vortex Vitality

MQTT gateway app to link **Vprobe** devices with **Home Assistant**.

This project was created to make it easier for Vortex Vitality customers (and the wider Home Assistant community) to integrate Vprobe sensor data into their smart home setups — and to invite contributions that add features others will also benefit from.

> ⚠️ Status: Early open-source release. Expect some rough edges while we turn internal assumptions into stable, documented interfaces.

---

## Quick start

### 1) Prerequisites

- A running **Home Assistant** instance. Can be a virtual machine or a physical server.
- Home Assistant **MQTT integration** configured (commonly via the Mosquitto add-on)
- A machine to run this broker (commonly Windows if this is built as a desktop app; can be adapted to run as a service later)
- Network access to your MQTT broker.

### 2) Build & run (developer workflow)

1. Clone this repository
2. Open `MQTT Broker by Vortex Vitality.sln` in Visual Studio
3. Restore NuGet packages (Right-click the Solution in `Solution Explorer` -> `Restore NuGet packages`)
4. Build and run

### 3) Using the App
1. In the **Vplants** mobile app: `Dashboard → Profile → Home Assistant → Generate Auth Code`  
   Copy the **6-digit Auth Code**.
2. In the Broker app:
   - Paste the **6-digit Auth Code** into the login input
   - Click **Login**  
   On success, the Broker receives your session info (UUID + JWT) and loads your Vprobe serial numbers into the list.
3. Broker IP (important): 

   The Broker runs a **local MQTT broker on port 1883** and binds it to the IPv4 you select in the IP dropdown.
   - If your PC has multiple IPv4 addresses (Wi-Fi/Ethernet), select the one that is reachable from your Home Assistant machine.
   - If you change the dropdown, the Broker restarts and re-binds.
4. In **Home Assistant → MQTT integration settings**, set:
   - **Broker host** = the same IPv4 you selected in the Broker app
   - **Port** = `1883`
5. Back in the Broker app:
   - Select the devices you want to link in the list
   - Click **Link Selected with Home Assistant**  
   This will:
   - Send your selected/rejected device list to the backend (`requestType = "getTopicName"`)
   - Subscribe the Broker to the **cloud MQTT topics** for selected devices
   - Publish **Home Assistant MQTT Discovery** config messages for the selected devices
   - Trigger a data refresh shortly after linking (server-side)
6. In Home Assistant:
   - Refresh the page or restart Home Assistant to see newly linked devices/entities
   - Keep the Broker running so sensor states continue updating

> Runtime: **.NET Framework 4.7.2+**

### 4) How the Broker works

This app is a **bridge** with three parts: (1) Vortex backend HTTP, (2) cloud MQTT (AWS IoT), (3) local MQTT (Home Assistant connects here).

#### 4.1 App settings (`App.config`)

`Form1.cs` reads these from `appSettings`:

- `VP_HA_FUNCTION_URL`  
  Backend endpoint used for:
  - `loggingIn` / `refresh` (GET) to fetch devices + session info
  - `getTopicName` (POST) to set selected/rejected devices and return the topic base
  - `forceDataUpdate` (POST) used shortly after linking to refresh data

- `AWS_IOT_ENDPOINT_HOST` + `AWS_AUTHORIZER`  
  Used to connect to AWS IoT via **MQTT over WebSockets** (TLS 1.2) using your `uuid` + `jwt`.

#### 4.2 Local Home Assistant connection (LAN side)

When running, the app starts a **local MQTT broker**:
- Binds to the selected IPv4
- Listens on port **1883**

The app also connects to that same local broker as a **client** (internally), and subscribes to:
- `homeassistant/status`

When it receives `homeassistant/status = online`, it:
- republishes MQTT Discovery configs, and
- ensures it’s connected to the cloud MQTT side and (re)subscribes to the selected device topics.

#### 4.3 Cloud MQTT connection (AWS side)

After login, the app connects to AWS IoT using:
- ClientId = `uuid`
- Credentials = (`uuid`, `jwt`)
- WebSocket URL built from `AWS_IOT_ENDPOINT_HOST` and `AWS_AUTHORIZER`

It subscribes to:
- `vv/ha/sensor/{uuid}/#` (wildcard)
- plus per-device topics returned by the backend (topic base + `/SN`) when you link devices

Incoming cloud messages are parsed and converted into a compact JSON “state” payload that the Broker publishes locally to:
- `VprobeSN{SN}/sensor/state` (retained)

State keys published (when present):
- `light`, `temp`, `humidity`, `moisture`, `batt`, `time` (epoch seconds)
- plus (only for SN ≥ 1000): `fertility`, `soilTemp`  
  (`soilTemp` is accepted from cloud as either `soilTemp` or legacy `soiltemp`)

#### 4.4 Entities created in Home Assistant (MQTT Discovery)

For every **linked** Vprobe SN, the Broker publishes retained discovery configs to topics like:
- `homeassistant/sensor/VprobeSN{SN}{objectIdSuffix}/config`

Each config points Home Assistant at the shared state topic:
- `state_topic`: `VprobeSN{SN}/sensor/state`

…and uses templates like:
- `{{ value_json.temp }}`, `{{ value_json.humidity }}`, etc.

Sensors published per device:
- Light (Lux)
- Temperature (°C)
- Humidity (%)
- Soil Moisture (%)
- Battery (V)
- Epoch (`time`, as integer epoch seconds)
- Fertility (µS/cm) **only if SN ≥ 1000**
- Soil Temperature (°C) **only if SN ≥ 1000**

If a device is **not linked** (or is rejected), the Broker removes the discovery entities by publishing **null retained** configs for that SN.



---

