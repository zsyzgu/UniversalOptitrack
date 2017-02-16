using System;
using System.Collections.Generic;
using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Net;
using System.Threading;
using System.Runtime.InteropServices;

using NatNetML;

namespace WinFormTestApp
{
    public partial class UniForm : Form
    {
        Server server;

        // [NatNet] Our NatNet object
        private NatNetML.NatNetClientML m_NatNet;
        
        // [NatNet] Our NatNet Frame of Data object
        private NatNetML.FrameOfMocapData m_FrameOfData = new NatNetML.FrameOfMocapData();
    
        // [NatNet] Description of the Active Model List from the server (e.g. Motive)
        NatNetML.ServerDescription desc = new NatNetML.ServerDescription();

        // [NatNet] Queue holding our incoming mocap frames the NatNet server (e.g. Motive)
        private Queue<NatNetML.FrameOfMocapData> m_FrameQueue = new Queue<NatNetML.FrameOfMocapData>();
        
        // spreadsheet lookup
        Hashtable htMarkers = new Hashtable();
        Hashtable htRigidBodies = new Hashtable();
        List<RigidBody> mRigidBodies = new List<RigidBody>();

        // graphing support
        const int GraphFrames = 500;
        long m_FrameCounter = 0;
        int m_iLastFrameNumber = 0;

        // frame timing information
        double m_fLastFrameTimestamp = 0.0f;
        float m_fCurrentMocapFrameTimestamp = 0.0f;
		float m_fFirstMocapFrameTimestamp = 0.0f;
        HiResTimer timer;
        Int64 lastTime = 0;

        // server information
        double m_ServerFramerate = 1.0f;
        float m_ServerToMillimeters = 1.0f;
        
        private static object syncLock = new object(); 
        private delegate void OutputMessageCallback(string strMessage);
        private bool needMarkerListUpdate = false;

        public UniForm()
        {
            InitializeComponent();
            timer = new HiResTimer();
            server = new Server();
        }

        //THE MOST IMPORTANT FUNCTION: to define the UniForm of broadcasting optitrack data.
        private void broadcast() {
            server.Send("framestart");
            FrameOfMocapData frame = m_FrameOfData;
            for (int i = 0; i < frame.nRigidBodies; ++i) {
                RigidBodyData rb = frame.RigidBodies[i];
                float[] quat = new float[4] { rb.qx, rb.qy, rb.qz, rb.qw };
                float[] eulers = m_NatNet.QuatToEuler(quat, (int)NATEulerOrder.NAT_XYZr);
                double rx = RadiansToDegrees(eulers[0]);
                double ry = RadiansToDegrees(eulers[1]);
                double rz = RadiansToDegrees(eulers[2]);
                server.Send("rb " + rb.x + " " + rb.y + " " + rb.z + " " + rx + " " + ry + " " + rz);
                /*for (int j = 0; j < rb.nMarkers; j++) {
                    Marker marker = rb.Markers[j];
                    server.Send("rbposition " + marker.x + " " + marker.y + " " + marker.z);
                }*/
            }
            /*for (int i = 0; i < frame.nOtherMarkers; ++i) {
                Marker marker = frame.OtherMarkers[i];
                server.Send("othermarker " + marker.x + " " + marker.y + " " + marker.z);
            }*/
            server.Send("frameend");
        }

        private void UniForm_Load(object sender, EventArgs e)
        {
            // Show available ip addresses of this machine
            String strMachineName = Dns.GetHostName();
            IPHostEntry ipHost = Dns.GetHostByName(strMachineName);
            foreach(IPAddress ip in ipHost.AddressList)
            {
                string strIP = ip.ToString();
                comboBoxLocal.Items.Add(strIP);
            }
            int selected = comboBoxLocal.Items.Add("127.0.0.1");
            comboBoxLocal.SelectedItem = comboBoxLocal.Items[selected];

            // create NatNet server
            int iConnectionType = 0;
            if (RadioUnicast.Checked)
                iConnectionType = 1;
            int iResult = CreateClient(iConnectionType);

            // create data chart
            chart1.Series[0].Points.Clear();
            chart1.ChartAreas[0].CursorX.IsUserSelectionEnabled = true;

        }

        private int CreateClient(int iConnectionType)
        {
            // release any previous instance
            if(m_NatNet != null)
            {
                m_NatNet.Uninitialize();
            }

            // [NatNet] create a new NatNet instance
            m_NatNet = new NatNetML.NatNetClientML(iConnectionType);

            // [NatNet] set a "Frame Ready" callback function (event handler) handler that will be
            // called by NatNet when NatNet receives a frame of data from the server application
            m_NatNet.OnFrameReady += new NatNetML.FrameReadyEventHandler(m_NatNet_OnFrameReady);
            
            /*
            // [NatNet] for testing only - event signature format required by some types of .NET applications (e.g. MatLab)
            m_NatNet.OnFrameReady2 += new FrameReadyEventHandler2(m_NatNet_OnFrameReady2);
            */

            // [NatNet] print version info
            int[] ver = new int[4];
            ver = m_NatNet.NatNetVersion();
            String strVersion = String.Format("NatNet Version : {0}.{1}.{2}.{3}", ver[0], ver[1],ver[2],ver[3]);
            OutputMessage(strVersion);
            return 0;
            
        }

        private void Connect()
        {
            // [NatNet] connect to a NatNet server
            int returnCode = 0;
            string strLocalIP = comboBoxLocal.SelectedItem.ToString();
            string strServerIP = textBoxServer.Text;
            returnCode = m_NatNet.Initialize(strLocalIP, strServerIP);
            if (returnCode == 0)
                OutputMessage("Initialization Succeeded.");
            else
            {
                OutputMessage("Error Initializing.");
                checkBoxConnect.Checked = false;
            }

            // [NatNet] validate the connection
            returnCode = m_NatNet.GetServerDescription(desc);
            if (returnCode == 0)
            {
                OutputMessage("Connection Succeeded.");
                OutputMessage("   Server App Name: " + desc.HostApp);
                OutputMessage(String.Format("   Server App Version: {0}.{1}.{2}.{3}", desc.HostAppVersion[0], desc.HostAppVersion[1], desc.HostAppVersion[2], desc.HostAppVersion[3]));
                OutputMessage(String.Format("   Server NatNet Version: {0}.{1}.{2}.{3}", desc.NatNetVersion[0], desc.NatNetVersion[1], desc.NatNetVersion[2], desc.NatNetVersion[3]));
                checkBoxConnect.Text = "Disconnect";

                // Tracking Tools and Motive report in meters - lets convert to millimeters
                if (desc.HostApp.Contains("TrackingTools") || desc.HostApp.Contains("Motive"))
                    m_ServerToMillimeters = 1000.0f;


                // [NatNet] [optional] Query mocap server for the current camera framerate
                int nBytes = 0;
                byte[] response = new byte[10000];
                int rc;
                rc = m_NatNet.SendMessageAndWait("FrameRate", out response, out nBytes);
                if (rc == 0)
                {
                    try
                    {
                        m_ServerFramerate = BitConverter.ToSingle(response, 0);
                        OutputMessage(String.Format("   Server Framerate: {0}", m_ServerFramerate));
                    }
                    catch (System.Exception ex)
                    {
                        OutputMessage(ex.Message);
                    }
                }

                m_fCurrentMocapFrameTimestamp = 0.0f;
                m_fFirstMocapFrameTimestamp = 0.0f;
            }
            else
            {
                OutputMessage("Error Connecting.");
                checkBoxConnect.Checked = false;
                checkBoxConnect.Text = "Connect";
            }

        }

        private void Disconnect()
        {
            // [NatNet] disconnect
            m_NatNet.Uninitialize();
            checkBoxConnect.Text = "Connect";
        }

        private void checkBoxConnect_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBoxConnect.Checked)
            {
                Connect();
            }
            else
            {
                Disconnect();
            }
        }

        private void OutputMessage(string strMessage)
        {
            if (this.listView1.InvokeRequired)
            {
                // It's on a different thread, so use Invoke.
                OutputMessageCallback d = new OutputMessageCallback(OutputMessage);
                this.Invoke(d, new object[] { strMessage});
            }
            else
            {
                // It's on the same thread, no need for Invoke
                DateTime d = DateTime.Now;
                String strTime = String.Format("{0}:{1}:{2}:{3}", d.Hour, d.Minute, d.Second, d.Millisecond);
                ListViewItem item = new ListViewItem(strTime, 0);
                item.SubItems.Add(strMessage);
                listView1.Items.Add(item);
            }

        }

        private RigidBody FindRB(int id)
        {
            foreach(RigidBody rb in mRigidBodies)
            {
                if(rb.ID == id)
                    return rb;
            }
            return null;
        }

        private void UpdateChart(long iFrame)
        {
            // Lets only show 500 frames at a time
            iFrame %= GraphFrames;

            // clear graph if we've wrapped, allow for fudge
            if( (m_iLastFrameNumber - iFrame) > 400)
            {
                chart1.Series[0].Points.Clear();
            }

            if (dataGridView1.SelectedCells.Count>0)
            {
                // use only the first selected cell
                DataGridViewCell cell = dataGridView1.SelectedCells[0];
                if (cell.Value == null)
                    return;
                double dValue = 0.0f;
                if (!Double.TryParse(cell.Value.ToString(), out dValue))
                    return;
                chart1.Series[0].Points.AddXY(iFrame, (float)dValue);

            }

            // update red 'cursor' line
            chart1.ChartAreas[0].CursorX.SetCursorPosition(iFrame);

            m_iLastFrameNumber = (int)iFrame;
        }

        private void UpdateDataGrid()
        {
            broadcast();

            for (int i = 0; i < m_FrameOfData.nMarkerSets; i++)
            {
                NatNetML.MarkerSetData ms = m_FrameOfData.MarkerSets[i];
                for (int j = 0; j < ms.nMarkers; j++)
                {
                    string strUniqueName = ms.MarkerSetName + j.ToString();
                    int key = strUniqueName.GetHashCode();
                    if(htMarkers.Contains(key))
                    {
                        int rowIndex = (int)htMarkers[key];
                        if (rowIndex >= 0)
                        {
                            dataGridView1.Rows[rowIndex].Cells[1].Value = ms.Markers[j].x;
                            dataGridView1.Rows[rowIndex].Cells[2].Value = ms.Markers[j].y;
                            dataGridView1.Rows[rowIndex].Cells[3].Value = ms.Markers[j].z;
                        }
                    }
                }
            }
            
            // update RigidBody data
            for (int i = 0; i < m_FrameOfData.nRigidBodies; i++)
            {
                NatNetML.RigidBodyData rb = m_FrameOfData.RigidBodies[i];              
                int key = rb.ID.GetHashCode();

                float[] quat2 = new float[4] { rb.qx, rb.qy, rb.qz, rb.qw };
                float[] euler2 = new float[3];
                euler2 = m_NatNet.QuatToEuler(quat2, (int)NATEulerOrder.NAT_XYZr);
                /*double x = RadiansToDegrees(eulers[0]);
                double y = RadiansToDegrees(eulers[1]);
                double z = RadiansToDegrees(eulers[2]);*/

                DateTime dt = DateTime.Now;
                int t = ((dt.Hour * 60 + dt.Minute) * 60 + dt.Second) * 1000 + dt.Millisecond;
                
                // note : must add rb definitions here one time instead of on get data descriptions because we don't know the marker list yet.
                if (!htRigidBodies.ContainsKey(key))
                {
                    // Add RigidBody def to the grid
                    if(rb.Markers[0].ID != -1)
                    {
                        RigidBody rbDef = FindRB(rb.ID);
                        if (rbDef != null)
                        {
                            int rowIndex = dataGridView1.Rows.Add("RigidBody: " + rbDef.Name);
                            key = rb.ID.GetHashCode();
                            htRigidBodies.Add(key, rowIndex);
                            // Add Markers associated with this rigid body to the grid
                            for (int j = 0; j < rb.nMarkers; j++)
                            {
                                String strUniqueName = rbDef.Name + "-" + rb.Markers[j].ID.ToString();
                                int keyMarker = strUniqueName.GetHashCode();
                                int newRowIndexMarker = dataGridView1.Rows.Add(strUniqueName);
                                htMarkers.Add(keyMarker, newRowIndexMarker);
                            }
                        }
                    }
                }
                else
                {
                    // update RigidBody data
                    int rowIndex = (int)htRigidBodies[key];
                    if(rowIndex >= 0)
                    {
                        dataGridView1.Rows[rowIndex].Cells[1].Value = rb.x * m_ServerToMillimeters;
                        dataGridView1.Rows[rowIndex].Cells[2].Value = rb.y * m_ServerToMillimeters;
                        dataGridView1.Rows[rowIndex].Cells[3].Value = rb.z * m_ServerToMillimeters;

                        // Convert quaternion to eulers.  Motive coordinate conventions: X(Pitch), Y(Yaw), Z(Roll), Relative, RHS
                        float[] quat = new float[4] {rb.qx, rb.qy, rb.qz, rb.qw};
                        float[] eulers = new float[3];
                        eulers = m_NatNet.QuatToEuler(quat, (int)NATEulerOrder.NAT_XYZr);
                        double x = RadiansToDegrees(eulers[0]);     // convert to degrees
                        double y = RadiansToDegrees(eulers[1]);
                        double z = RadiansToDegrees(eulers[2]);

                        dataGridView1.Rows[rowIndex].Cells[4].Value = x;
                        dataGridView1.Rows[rowIndex].Cells[5].Value = y;
                        dataGridView1.Rows[rowIndex].Cells[6].Value = z;

                        // update Marker data associated with this rigid body
                        for (int j = 0; j < rb.nMarkers; j++)
                        {
                            if (rb.Markers[j].ID != -1)
                            {
                                RigidBody rbDef = FindRB(rb.ID);
                                if (rbDef != null)
                                {
                                    String strUniqueName = rbDef.Name + "-" + rb.Markers[j].ID.ToString();
                                    int keyMarker = strUniqueName.GetHashCode();
                                    if (htMarkers.ContainsKey(keyMarker))
                                    {
                                        int rowIndexMarker = (int)htMarkers[keyMarker];
                                        NatNetML.Marker m = rb.Markers[j];
                                        dataGridView1.Rows[rowIndexMarker].Cells[1].Value = m.x;
                                        dataGridView1.Rows[rowIndexMarker].Cells[2].Value = m.y;
                                        dataGridView1.Rows[rowIndexMarker].Cells[3].Value = m.z;
                                    }
                                }
                            }
                        }
                    }
                }   
            }
        }

        private void buttonGetDataDescriptions_Click(object sender, EventArgs e)
        {
            mRigidBodies.Clear();
            dataGridView1.Rows.Clear();
            htMarkers.Clear();
            htRigidBodies.Clear();
            needMarkerListUpdate = true;

            OutputMessage("Retrieving Data Descriptions....");
            List<NatNetML.DataDescriptor> descs = new List<NatNetML.DataDescriptor>();
            bool bSuccess = m_NatNet.GetDataDescriptions(out descs);
            if(bSuccess)
            {
                OutputMessage(String.Format("Retrieved {0} Data Descriptions....", descs.Count));
                int iObject = 0;
                foreach (NatNetML.DataDescriptor d in descs)
                {
                    iObject++;

                    // MarkerSets
                    if (d.type == (int)NatNetML.DataDescriptorType.eMarkerSetData)                    
                    {
                        NatNetML.MarkerSet ms = (NatNetML.MarkerSet)d;                       
                        OutputMessage("Data Def " + iObject.ToString() + " [MarkerSet]");
                        
                        OutputMessage(" Name : " + ms.Name);
                        OutputMessage(String.Format(" Markers ({0}) ",ms.nMarkers));
                        dataGridView1.Rows.Add("MarkerSet: " + ms.Name);
                        for(int i=0; i<ms.nMarkers; i++)
                        {
                            OutputMessage(("  " + ms.MarkerNames[i]));
                            int rowIndex = dataGridView1.Rows.Add("  " + ms.MarkerNames[i]);
                            // MarkerNameIndexToRow map
                            String strUniqueName = ms.Name + i.ToString();
                            int key = strUniqueName.GetHashCode();
                            htMarkers.Add(key, rowIndex); 
                        }
                    }
                    // RigidBodies
                    else if (d.type == (int)NatNetML.DataDescriptorType.eRigidbodyData)             
                    {
                        NatNetML.RigidBody rb = (NatNetML.RigidBody)d;

                        OutputMessage("Data Def " + iObject.ToString() + " [RigidBody]");
                        OutputMessage(" Name : " + rb.Name);
                        OutputMessage(" ID : " + rb.ID);
                        OutputMessage(" ParentID : " + rb.parentID);
                        OutputMessage(" OffsetX : " + rb.offsetx);
                        OutputMessage(" OffsetY : " + rb.offsety);
                        OutputMessage(" OffsetZ : " + rb.offsetz);
                     
                        mRigidBodies.Add(rb);

                        int rowIndex = dataGridView1.Rows.Add("RigidBody: "+rb.Name);
                        // RigidBodyIDToRow map
                        int key = rb.ID.GetHashCode();
                        htRigidBodies.Add(key, rowIndex);

                    }
                    // Skeletons
                    else if (d.type == (int)NatNetML.DataDescriptorType.eSkeletonData)
                    {
                        NatNetML.Skeleton sk = (NatNetML.Skeleton)d;

                        OutputMessage("Data Def " + iObject.ToString() + " [Skeleton]");
                        OutputMessage(" Name : " + sk.Name);
                        OutputMessage(" ID : " + sk.ID);
                        dataGridView1.Rows.Add("Skeleton: " + sk.Name);
                        for (int i = 0; i < sk.nRigidBodies; i++)
                        {
                            RigidBody rb = sk.RigidBodies[i];
                            OutputMessage(" RB Name  : " + rb.Name);
                            OutputMessage(" RB ID    : " + rb.ID);
                            OutputMessage(" ParentID : " + rb.parentID);
                            OutputMessage(" OffsetX  : " + rb.offsetx);
                            OutputMessage(" OffsetY  : " + rb.offsety);
                            OutputMessage(" OffsetZ  : " + rb.offsetz);
                            int rowIndex = dataGridView1.Rows.Add("Bone: " + rb.Name);
                            // RigidBodyIDToRow map
                            int uniqueID = sk.ID * 1000 + rb.ID;
                            int key = uniqueID.GetHashCode();
                            if (htRigidBodies.ContainsKey(key))
                                MessageBox.Show("Duplicate RigidBody ID");
                            else
                                htRigidBodies.Add(key, rowIndex);
                        }
                    }
                    else
                    {
                        OutputMessage("Unknown DataType");
                    }
                }
            }
            else
            {
                OutputMessage("Unable to retrieve DataDescriptions");
            }
        }

        void m_NatNet_OnFrameReady(NatNetML.FrameOfMocapData data, NatNetML.NatNetClientML client)
        {
            // [optional] High-resolution frame arrival timing information
            Int64 currTime = timer.Value;
            if(lastTime !=0)
            {
                // Get time elapsed in tenths of a millisecond.
                Int64 timeElapsedInTicks = currTime - lastTime;
                Int64 timeElapseInTenthsOfMilliseconds = (timeElapsedInTicks * 10000) / timer.Frequency;
                // uncomment for timing info
                //OutputMessage("Frame Delivered: (" + timeElapseInTenthsOfMilliseconds.ToString() + ")  FrameTimestamp: " + data.fLatency);
            }

            // [NatNet] Add the incoming frame of mocap data to our frame queue,  
            // Note: the frame queue is a shared resource with the UI thread, so lock it while writing
            lock(syncLock)
            {
                // [optional] clear the frame queue before adding a new frame
                m_FrameQueue.Clear();
                m_FrameQueue.Enqueue(data);
            }
            lastTime = currTime;
           // MessageBox.Show(data.ToString());
        }

        // [NatNet] [optional] alternate function signatured frame ready callback handler for .NET applications/hosts
        // that don't support the m_NatNet_OnFrameReady defiend above (e.g. MATLAB)
        void m_NatNet_OnFrameReady2(object sender, NatNetEventArgs e)
        {
            m_NatNet_OnFrameReady(e.data, e.client);
        }

        int cnt = 0;
        private void UpdateUITimer_Tick(object sender, EventArgs e)
        {
            // the frame queue is a shared resource with the FrameOfMocap delivery thread, so lock it while reading
            // note this can block the frame delivery thread.  In a production application frame queue management would be optimized.
            lock (syncLock)
            {
                while (m_FrameQueue.Count > 0)
                {
                    m_FrameOfData = m_FrameQueue.Dequeue();
                    
                    if (m_FrameQueue.Count > 0)
                        continue;
                    
                    if(m_FrameOfData != null)
                    {
                        // for servers that only use timestamps, not frame numbers, calculate a 
                        // frame number from the time delta between frames
                        if (desc.HostApp.Contains("TrackingTools") || desc.HostApp.Contains("Motive"))
                        {                      
                            m_fCurrentMocapFrameTimestamp = m_FrameOfData.fLatency;
                            if (m_fCurrentMocapFrameTimestamp == m_fLastFrameTimestamp)
                            {
                                continue;
                            }
                            if (m_fFirstMocapFrameTimestamp == 0.0f)
                            {
                                m_fFirstMocapFrameTimestamp = m_fCurrentMocapFrameTimestamp;
                            }
                            m_FrameOfData.iFrame = (int)((m_fCurrentMocapFrameTimestamp - m_fFirstMocapFrameTimestamp) * m_ServerFramerate);

                        }

                        // update the data grid
                        Console.WriteLine(cnt++);
                        UpdateDataGrid();

                        // update the chart
                        UpdateChart(m_FrameCounter++);
                        
                        // only redraw chart when necessary, not for every frame
                        if (m_FrameQueue.Count == 0)
                        {
                            chart1.ChartAreas[0].RecalculateAxesScale();
                            chart1.ChartAreas[0].AxisX.Minimum = 0;
                            chart1.ChartAreas[0].AxisX.Maximum = GraphFrames;
                            chart1.Invalidate();
                        }
                        
                        // Mocap server timestamp (in seconds)
                        m_fLastFrameTimestamp = m_FrameOfData.fTimestamp;
                        TimestampValue.Text = m_FrameOfData.fTimestamp.ToString("F3");

                        // SMPTE timecode (if timecode generator present)
                        int hour, minute, second, frame, subframe;
                        bool bSuccess = m_NatNet.DecodeTimecode(m_FrameOfData.Timecode, m_FrameOfData.TimecodeSubframe, out hour, out minute, out second, out frame, out subframe);
                        if(bSuccess)
                            TimecodeValue.Text = string.Format("{0:D2}:{1:D2}:{2:D2}:{3:D2}.{4:D2}",hour, minute, second, frame, subframe);

                        if (m_FrameOfData.bRecording)
                            chart1.BackColor = Color.Red;
                        else
                            chart1.BackColor = Color.White;
                    }
                }
            }
        }

        double RadiansToDegrees(double dRads)
        {
            return dRads * (180.0f / Math.PI);
        }

        private void UniForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            m_NatNet.Uninitialize();
        }

        private void RadioMulticast_CheckedChanged(object sender, EventArgs e)
        {
            bool bNeedReconnect = checkBoxConnect.Checked;
            int iResult = CreateClient(0);
            if (bNeedReconnect)
                Connect();               
        }

        private void RadioUnicast_CheckedChanged(object sender, EventArgs e)
        {
            bool bNeedReconnect = checkBoxConnect.Checked;
            int iResult = CreateClient(1);
            if (bNeedReconnect)
                Connect();                         
        }

        public int LowWord(int number)
        {
            return number & 0xFFFF; 
        }

        public int HighWord(int number)
        {
            return ((number >> 16) & 0xFFFF); 
        }

        private void RecordButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            int rc = m_NatNet.SendMessageAndWait("StartRecording", 1, 20, out response, out nBytes);
            if( (rc!=0) || (response[0] !=1) )
            {
            
            }
        }

        private void StopRecordButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            int rc = m_NatNet.SendMessageAndWait("StopRecording", out response, out nBytes);

        }

        private void LiveModeButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            int rc = m_NatNet.SendMessageAndWait("LiveMode", out response, out nBytes);

        }

        private void EditModeButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            int rc = m_NatNet.SendMessageAndWait("EditMode", out response, out nBytes);

        }

        private void TimelinePlayButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            int rc = m_NatNet.SendMessageAndWait("TimelinePlay", out response, out nBytes);
        }

        private void TimelineStopButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            int rc = m_NatNet.SendMessageAndWait("TimelineStop", out response, out nBytes);
        }

        private void SetRecordingTakeButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            String strCommand = "SetRecordTakeName," + RecordingTakeNameText.Text;
            int rc = m_NatNet.SendMessageAndWait(strCommand, out response, out nBytes);
        }

        private void SetPlaybackTakeButton_Click(object sender, EventArgs e)
        {
            int nBytes = 0;
            byte[] response = new byte[10000];
            String strCommand = "SetPlaybackTakeName," + PlaybackTakeNameText.Text;
            int rc = m_NatNet.SendMessageAndWait(strCommand, out response, out nBytes);
        }
    }

    public class HiResTimer
    {
        private bool isPerfCounterSupported = false;
        private Int64 frequency = 0;

        private const string lib = "Kernel32.dll";
        [DllImport(lib)]
        private static extern int QueryPerformanceCounter(ref Int64 count);
        [DllImport(lib)]
        private static extern int QueryPerformanceFrequency(ref Int64 frequency);

        public HiResTimer()
        {
            int returnVal = QueryPerformanceFrequency(ref frequency);

            if (returnVal != 0 && frequency != 1000)
            {
                // The performance counter is supported.
                isPerfCounterSupported = true;
            }
            else
            {
                // The performance counter is not supported. Use 
                // Environment.TickCount instead.
                frequency = 1000;
            }
        }

        public Int64 Frequency
        {
            get
            {
                return frequency;
            }
        }

        public Int64 Value
        {
            get
            {
                Int64 tickCount = 0;

                if (isPerfCounterSupported)
                {
                    // Get the value here if the counter is supported.
                    QueryPerformanceCounter(ref tickCount);
                    return tickCount;
                }
                else
                {
                    // Otherwise, use Environment.TickCount. 
                    return (Int64)Environment.TickCount;
                }
            }
        }
    }
}