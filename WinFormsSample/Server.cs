using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;

namespace WinFormTestApp
{
    class Server
    {
        public string serverIp = "192.168.1.129";
        public int serverPort = 7643;
        public TcpListener tcpListener;
        public List<TcpClient> clientList = new List<TcpClient>();
        public object clientListMutex = new object();
        
        public Server()
        {
            tcpListener = new TcpListener(IPAddress.Parse(serverIp), serverPort);
            Thread listenThread = new Thread(ListenThread);
            listenThread.Start();
        }

        public void ListenThread()
        {
            tcpListener.Start();
            Console.WriteLine("Listening ...");
            while (true)
            {
                TcpClient client = tcpListener.AcceptTcpClient();
                lock (clientListMutex)
                {
                    Console.WriteLine("client join");
                    clientList.Add(client);
                }
                Thread receiveThread = new Thread(ReceiveThread);
                receiveThread.Start(client);
            }
        }

        public void ReceiveThread(object clientObject)
        {
            TcpClient client = (TcpClient)clientObject;
            StreamReader streamReader = new StreamReader(client.GetStream());
            while (true)
            {
                try
                {
                    string line = streamReader.ReadLine();
                }
                catch
                {
                    break;
                }
            }
            lock (clientListMutex)
            {
                Console.WriteLine("client exit");
                clientList.Remove(client);
            }
        }

        public void Send(string info)
        {
            lock (clientListMutex)
            {
                foreach (TcpClient client in clientList)
                {
                    try
                    {
                        StreamWriter streamWriter = new StreamWriter(client.GetStream());
                        streamWriter.WriteLine(info);
                        streamWriter.Flush();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                    }
                }
            }
        }
    }
}
