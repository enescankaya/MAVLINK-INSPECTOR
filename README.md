# MAVLink Inspector

MAVLink Inspector is a tool built with C# and WPF, inspired by MissionPlanner's MAVLink Inspector. It allows users to monitor, analyze, and debug MAVLink messages in real-time. The application is designed to be lightweight, user-friendly, and highly customizable.

## Features

- **Real-Time Message Monitoring**: Displays incoming and outgoing MAVLink messages in a structured tree format.
- **Frequency (Hz) Calculation**: Measures the frequency of specific MAVLink messages.
- **Data Rate (BPS) Calculation**: Tracks the data transmission rate of messages.
- **Hierarchical Tree View**: Organizes messages by System ID, Component ID, and Message ID.
- **Filtering and Searching**: Enables filtering by sysid, compid, or msgid.
- **GCS Traffic Analysis**: Option to view Ground Control Station (GCS) messages.
- **Customizable UI**: Leverages WPF for a modern and responsive design.

## Getting Started

### Prerequisites

To run this project, you will need:

- [Visual Studio 2022](https://visualstudio.microsoft.com/) with .NET Desktop Development workload
- .NET SDK 8.0 or later
- MAVLink .NET library, such as [MAVLink.NET](https://github.com/ArduPilot/MAVLink.NET)

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/enescankaya/MAVLINK-INSPECTOR.git
