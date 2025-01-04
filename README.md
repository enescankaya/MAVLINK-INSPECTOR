# MAVLink Inspector

A MAVLink Inspector tool built with C# and WPF, inspired by MissionPlanner's MAVLink Inspector. This application allows users to monitor, analyze, and debug MAVLink messages in real-time. It is designed to be lightweight, customizable, and easy to use.

---

## 📋 Features

- **Real-Time MAVLink Message Monitoring**: View incoming and outgoing MAVLink messages in a structured tree format.
- **Frequency (Hz) Calculation**: Analyze the frequency of specific MAVLink messages.
- **Data Rate (BPS) Calculation**: Monitor the data transmission rate for individual messages.
- **Hierarchical Tree View**: Messages organized by System ID, Component ID, and Message ID.
- **Filtering and Searching**: Filter messages based on specific parameters like sysid, compid, or msgid.
- **GCS Traffic Analysis**: Option to display messages sent by the Ground Control Station (GCS).
- **Customizable UI**: Designed with WPF for a modern and responsive interface.

---

## 🚀 Getting Started

### Prerequisites

Ensure you have the following installed:
- [Visual Studio 2022](https://visualstudio.microsoft.com/) with .NET Desktop Development workload
- .NET SDK 8.0 or later
- MAVLink .NET library (e.g., [MAVLink.NET](https://github.com/ArduPilot/MAVLink.NET))

### Installation

1. Clone the repository:
   ```bash
   git clone https://github.com/enescankaya/MAVLINK-INSPECTOR.git
Open the project in Visual Studio.
Restore NuGet packages:
bash
Kodu kopyala
dotnet restore
Build the project:
bash
Kodu kopyala
dotnet build
🖥️ Usage
Run the application:
bash
Kodu kopyala
dotnet run
Connect your MAVLink-compatible device.
The messages will appear in the tree view organized by System ID, Component ID, and Message ID.
Enable Show GCS Traffic to view messages sent by the Ground Control Station.
📂 Project Structure
/MavlinkInspector/: Main application code.
/MavlinkInspector/ViewModels/: ViewModel layer for MVVM architecture.
/MavlinkInspector/Views/: XAML views for the WPF interface.
/MavlinkInspector/Utilities/: Helper classes for MAVLink message parsing and processing.
🤝 Contributing
Contributions are welcome! Please follow these steps:

Fork the repository.
Create a feature branch:
bash
Kodu kopyala
git checkout -b feature-branch-name
Commit your changes:
bash
Kodu kopyala
git commit -m "Add some feature"
Push to the branch:
bash
Kodu kopyala
git push origin feature-branch-name
Create a pull request.
🛠️ Built With
C# - Core programming language
WPF - Windows Presentation Foundation for UI
MAVLink.NET - MAVLink protocol implementation for .NET
MVVM - Architectural pattern
📜 License
This project is licensed under the MIT License. See the LICENSE file for details.

🌟 Acknowledgments
Inspired by MissionPlanner.
Thanks to the MAVLink team for the protocol and documentation.
🔗 Repository Link
https://github.com/enescankaya/MAVLINK-INSPECTOR
