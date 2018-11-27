using System;
using System.ComponentModel;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Listen_winform_V1.Models;
using Listen_winform_V1.Properties;

namespace Listen_winform_V1
{
    public partial class Form1 : Form
    {
        #region 給TCP用的宣告
        private Socket mainSocket;  // 宣告Socket 類 mainSocket 為 class 變數
        int port = 4800;
        const int MAX_CLIENTS = 24; // 定義最大Clients 常數
        private Socket[] workerSocket = new Socket[MAX_CLIENTS]; // 定義每個連線的工作類別域變數 Socket陣列
        #endregion

        #region 執行緒用的宣告
        Thread othread; //新的執行緒
        Thread othreadDisconnectCheck;//檢查斷線執行續
        #endregion

        #region Entity資料的宣告
        private CTKEntity db = new CTKEntity();
        #endregion


        public Form1()
        {
            //從Properties.Settings.Default.Language抓出語系
            Thread.CurrentThread.CurrentCulture = new CultureInfo(Properties.Settings.Default.Language);
            Thread.CurrentThread.CurrentUICulture = new CultureInfo(Properties.Settings.Default.Language);
            InitializeComponent();
        }

        //加入資源檔
        ResourceManager rm = new ResourceManager(typeof(Listen_winform_V1.Properties.Resources));

        /// <summary>
        /// 伺服函式，啟動監聽
        /// </summary>
        public void Server()
        {
            // 實體化 mainSocket
            mainSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            //建立本機監聽位址及埠號，IPAddress.Any表示監聽所有的介面。
            IPEndPoint ipLocal = new IPEndPoint(IPAddress.Any, port);

            //socket連繫到該位址
            mainSocket.Bind(ipLocal);

            // 啟動監聽
            //backlog=4 參數會指定可在佇列中等候接收的輸入連接數。若要決定可指定的最大連接數，
            //除非同時間的連線非常的大，否則值4應該很夠用。
            mainSocket.Listen(4);

            // 以上完成了SERVER的Listening，BeginAccept() 開啟執行緒準備接收client的要求。
            // 此處的BeginAccept 第一個參數就是當Socket一開始接收到Client的連線要求時，
            //立刻會呼叫delegate的OnClientConnect的方法，這個OnClientConnect名稱是我們自己取的，
            //你也可以叫用別的名稱。因此我們要另外寫一個OnClientConnect()的函式。
            mainSocket.BeginAccept(new AsyncCallback(OnClientConnect), null);
        }

        /// <summary>
        /// 當客戶端建立連線時叫用
        /// </summary>
        /// <param name="asyn"></param>
        public void OnClientConnect(IAsyncResult asyn)
        {
            try
            {
                //宣告並定義空頻道的ID=-1，表示無空頻道資料
                int Empty_channel_ID = -1;
                //若主Socket為空則跳出
                if (mainSocket == null) return;

                Socket temp_Socket = mainSocket.EndAccept(asyn);

                //取得遠端節點的EndPoint
                EndPoint RemoteEP = temp_Socket.RemoteEndPoint;

                Empty_channel_ID = FindEmptyChannel();
                if (Empty_channel_ID == -1) throw new NoSocketAvailableException();

                //將方才暫存的Socket交給空的 Socket接收
                workerSocket[Empty_channel_ID] = temp_Socket;

                //將暫存的Socket設為空
                temp_Socket = null;

                WaitForData(workerSocket[Empty_channel_ID]);
            }
            //catch (ObjectDisposedException) { …處理已釋放記憶體的資源例外處理略... }
            //catch (SocketException) { …因TCP Socket造成的例外處理略... }
            //自定了一個錯誤的Exception類型，當找不到空頻道表示所有頻道都被佔用
            //catch (NoSocketAvailableException err) { …無可用的頻道例外處理略... }
            finally
            {
                //將方才關閉的主要Socket重新接收新的連線
                mainSocket.BeginAccept(new AsyncCallback(OnClientConnect), null);
            }
        }

        //宣告AsyncCallback類別的變數 pfnWorkerCallBack
        public AsyncCallback pfnWorkerCallBack;

        /// <summary>
        /// 建立連線等待資料傳入
        /// </summary>
        /// <param name="soc"></param>
        public void WaitForData(System.Net.Sockets.Socket soc)
        {
            try
            {
                //當pfnWorkerCallBack物件尚未實體化時，進行實體化
                if (pfnWorkerCallBack == null)
                {
                    pfnWorkerCallBack = new AsyncCallback(OnDataReceived);
                }
                //自行定義的型別 SocketPacket，附於此小節尾，內容只有一個Socket類和一個int。
                SocketPacket theSocPkt = new SocketPacket();
                //指定此一建立連線之Socket soc 給定義的 theSocPkt
                theSocPkt.m_currentSocket = soc;

                soc.BeginReceive(theSocPkt.dataBuffer, 0, theSocPkt.dataBuffer.Length, SocketFlags.None, pfnWorkerCallBack, theSocPkt);
            }
            catch (SocketException) { }
        }

        //自型定義的物件封包的類別
        public class SocketPacket
        {
            //目前Activating之Socket
            public System.Net.Sockets.Socket m_currentSocket;
            public byte[] dataBuffer = new byte[1024]; //接受資料陣列
        }

        public class NoSocketAvailableException : System.Exception
        {
            public new string Message = "所有的頻道都已佔滿，請先釋放其他的頻道";
        }

        private Socket[] m_workerSocket = new Socket[MAX_CLIENTS];
        // 尋找第一個空頻道的函式，傳回Channel ID或是-1 全被佔滿
        /// <summary>
        /// 尋找未用的頻道
        /// </summary>
        /// <returns></returns>
        private int FindEmptyChannel()
        {
            for (int i = 0; i < MAX_CLIENTS; i++)
            {
                if (m_workerSocket[i] == null || !m_workerSocket[i].Connected)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        ///  已連線連線並有資料傳入時叫用
        /// </summary>
        /// <param name="asyn"></param>
        public void OnDataReceived(IAsyncResult asyn)
        {
            try
            {
                SocketPacket socketData = (SocketPacket)asyn.AsyncState;//取得接受的資料
                //宣告及定義訊息長度
                int iRx = socketData.m_currentSocket.EndReceive(asyn);

                var clientIP = socketData.m_currentSocket.RemoteEndPoint; //客戶端IP

                byte[] databuff = socketData.dataBuffer;

                //回應丟去Helper來判斷原因，把原因切割成許多內容

                //開頭是55AAFE才收，但是第四五碼是A3B6(163、182)時不收
                if (databuff[0] == 85 && databuff[1] == 170 && databuff[2] == 254 && databuff[3] != 163 && databuff[4] != 182)
                {
                    var response = ServerResponseHelper.cutData(databuff);


                    var timeNow = DateTime.Now;
                    var timeNowS = timeNow;

                    response.ip = Convert.ToString(clientIP).Split(':')[0]; //只取IP
                    response.InsertDate = timeNow;

                    //IP-完整內容-類型1-類型2-說明
                    var msg = $"[{timeNowS}]-{response.ip}-{response.Cate}-{response.replayString}";


                    try
                    {
                        //通報別的執行緒上的控制象進行顯示
                        Print(tb_result, msg);

                        //存入資料庫
                        db.Events.Add(response);
                        db.SaveChanges();

                        //發信
                        MailHelper.SentMail(response.Cate,
                            "[" + response.InsertDate + "] " + response.ip + " " + response.replayString);

                    }
                    catch (Exception e)
                    {

                    }


                }

                //如果是回55AAFE A3B6就代表設備還在線上
                if (databuff[0] == 85 && databuff[1] == 170 && databuff[2] == 254 && databuff[3] == 163 &&
                    databuff[4] == 182)
                {
                    try
                    {

                        //收到資料先檢查暫存檔有沒有資料
                        var clientIPString = Convert.ToString(clientIP).Split(':')[0];
                        var tempRepeat = db.TempIO.Where(o => o.IP == clientIPString);

                        if (!tempRepeat.Any()) //如果沒資料的話
                        {
                            //加入資料庫
                            var tempdata = new TempIO
                            {
                                IP = Convert.ToString(clientIP).Split(':')[0],
                                InsertTime = DateTime.UtcNow,
                                DueTime = DateTime.UtcNow.AddSeconds(70),
                            };
                            db.TempIO.Add(tempdata);
                            db.SaveChanges();

                            //產生一個事件
                            var fullResponse = TransferHelper.ToHexString(databuff).Replace(" ", "").Substring(0, 20);

                            var tempEvent = new Events
                            {
                                ip = Convert.ToString(clientIP).Split(':')[0],
                                replay = fullResponse,
                                Cate = rm.GetString("CMD_Scanner"),
                                cate1 = "A3B6",
                                cate2 = "00",
                                replayString = rm.GetString("CMD_A3B6I"),
                                InsertDate = DateTime.Now
                            };
                            db.Events.Add(tempEvent);
                            db.SaveChanges();

                            //發信
                            MailHelper.SentMail(rm.GetString("CMD_A3B6I"),
                                "[" + tempEvent.InsertDate + "] " + tempEvent.ip + " " + rm.GetString("CMD_A3B6I"));

                            //IP-完整內容-類型1-類型2-說明
                            var msg = $"[{tempEvent.InsertDate}]-{tempEvent.ip}-{tempEvent.Cate}-{tempEvent.replayString}";

                            //通報別的執行緒上的控制象進行顯示
                            Print(tb_result, msg);
                        }
                        else
                        //如果暫存檔有的話，就更新時間
                        {
                            var saveTemp = tempRepeat.SingleOrDefault();
                            saveTemp.DueTime = DateTime.UtcNow.AddSeconds(70);
                            db.SaveChanges();
                        }
                    }
                    catch (Exception e)
                    {
                    }

                }

                WaitForData(socketData.m_currentSocket);
            }
            catch (SocketException) { }
        }

        //建立委派，用來呼叫別的執行緒時使用
        delegate void PrintHandler(TextBox tb, string text);

        //實做委派的內容
        private static void Print(TextBox tb, string text)
        {
            //判斷Textbox是否在同一個執行緒上
            if (tb.InvokeRequired)
            {
                //如果不在同一個執行緒的話，就觸發委派
                PrintHandler ph = new PrintHandler(Print);
                tb.Invoke(ph, tb, text);
            }
            else
            {
                //如果是的話，就直接顯示，通常不會這麼剛好
                tb.Text = tb.Text + text + Environment.NewLine;
            }
        }




        private void listen_start_Click(object sender, EventArgs e)
        {
            try
            {
                //先清除TempIO裡的所有內容，重新開始紀錄連線與斷線
                //刪除所有資料
                var rows = db.TempIO;
                foreach (var row in rows)
                {
                    db.TempIO.Remove(row);
                }
                db.SaveChanges();

                //Server();
                listen_start.Enabled = false;

                //告訴執行緒要執行Server()這個方法
                othread = new Thread(Server);
                //開始執行
                othread.Start();

                othreadDisconnectCheck = new Thread(DisconnectCheck);
                othreadDisconnectCheck.Start();
            }
            catch (Exception exception)
            {

            }
        }

        /// <summary>
        /// 檢查重複
        /// </summary>
        private void DisconnectCheck()
        {
            while (true)
            {
                try
                {
                    //把時間小於Due的抓出來
                    var tempList = db.TempIO.Where(o => o.DueTime <= DateTime.UtcNow).ToList();


                    foreach (var SingleData in tempList)
                    {

                        //把這筆從暫存拿掉
                        db.TempIO.Remove(SingleData);

                        //建立一個斷線事件
                        var tempEvent = new Events
                        {
                            ip = SingleData.IP,
                            replay = "",
                            Cate = rm.GetString("CMD_Scanner"),
                            cate1 = "A3B6",
                            cate2 = "00",
                            replayString = rm.GetString("CMD_A3B6O"),
                            InsertDate = DateTime.Now
                        };
                        db.Events.Add(tempEvent);
                        db.SaveChanges();

                        //發信
                        MailHelper.SentMail(rm.GetString("CMD_A3B6O"),
                            "[" + tempEvent.InsertDate + "] " + tempEvent.ip + " " + rm.GetString("CMD_A3B6O"));


                        //顯示在畫面上
                        //IP-完整內容-類型1-類型2-說明
                        var msg = $"[{tempEvent.InsertDate}]-{tempEvent.ip}-{tempEvent.Cate}-{tempEvent.replayString}";

                        //通報別的執行緒上的控制象進行顯示
                        Print(tb_result, msg);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }

                System.Threading.Thread.Sleep(60 * 1000); //6秒鐘發一次
            }
        }

        private void cb_language_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cb_language.Text == "English")
            {
                Properties.Settings.Default.Language = "en-GB";
                Properties.Settings.Default.Save();
            }
            else if (cb_language.Text == "繁體中文")
            {
                Properties.Settings.Default.Language = "zh-TW";
                Properties.Settings.Default.Save();
            }
            else if (cb_language.Text == "简体中文")
            {
                Properties.Settings.Default.Language = "zh-CN";
                Properties.Settings.Default.Save();
            }

        }

        private void Form1_Load(object sender, EventArgs e)
        {


            switch (Properties.Settings.Default.Language)
            {
                case "en-GB":
                    cb_language.Text = "English";
                    break;
                case "zh-TW":
                    cb_language.Text = "繁體中文";
                    break;
                case "zh-CN":
                    cb_language.Text = "简体中文";
                    break;
            }

            //讀取寄件伺服器資訊
            loadMailServer();


            //ResourceManager rm = new ResourceManager(typeof(Listen_winform_V1.Form1));
            //tb_result.Text = rm.GetString("listen_start.Text");
        }

        private void loadMailServer()
        {
            //讀取寄件人資訊
            tb_mail.Text = Properties.Settings.Default.Mail;
            tb_PSW.Text = Properties.Settings.Default.MailPSW;
            tb_host.Text = Properties.Settings.Default.MailHost;
            tb_port.Text = Properties.Settings.Default.MailPort;
            cbSSL.Checked = Properties.Settings.Default.MailSSL;
            cb_enable.Checked = Properties.Settings.Default.MailEnable;
        }

        private void btn_switch_Click(object sender, EventArgs e)
        {
            //重開程式，會用新的語系
            System.Diagnostics.Process.Start(System.Reflection.Assembly.GetExecutingAssembly().Location);

            //把自己消滅，以免監聽時占port
            //關閉執行緒
            try
            {
                othread.Abort();
                othreadDisconnectCheck.Abort();
            }
            catch (Exception exception)
            {
            }
            Dispose();

        }


        private void btn_save_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.Mail = tb_mail.Text;
            Properties.Settings.Default.MailPSW = tb_PSW.Text;
            Properties.Settings.Default.MailHost = tb_host.Text;
            Properties.Settings.Default.MailPort = tb_port.Text;
            Properties.Settings.Default.MailSSL = cbSSL.Checked;
            Properties.Settings.Default.MailEnable = cb_enable.Checked;
            Properties.Settings.Default.Save();
            //讀取寄件伺服器資訊
            loadMailServer();

        }

        private void btn_test_Click(object sender, EventArgs e)
        {
            try
            {
                System.Net.Mail.MailMessage msg = new System.Net.Mail.MailMessage();
                msg.To.Add(tb_mail.Text);
                msg.From = new MailAddress(tb_mail.Text, tb_mail.Text, System.Text.Encoding.UTF8);
                msg.Subject = "Test Mail";//郵件標題
                msg.SubjectEncoding = System.Text.Encoding.UTF8;//郵件標題編碼
                msg.Body = "Test Content"; //郵件內容
                msg.BodyEncoding = System.Text.Encoding.UTF8;//郵件內容編碼 
                msg.IsBodyHtml = true;//是否是HTML郵件 
                SmtpClient client = new SmtpClient();
                client.Credentials = new System.Net.NetworkCredential(tb_mail.Text, tb_PSW.Text); //這裡要填正確的帳號跟密碼
                client.Host = tb_host.Text; //設定smtp Server
                client.Port = Convert.ToInt32(tb_port.Text); //設定Port
                client.EnableSsl = cbSSL.Checked; //gmail預設開啟驗證
                client.Send(msg); //寄出信件
                client.Dispose();
                msg.Dispose();
                MessageBox.Show(rm.GetString("CMD_MailSuccess"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        /// <summary>
        /// 主畫面調整大小時
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Form1_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                this.Hide();
                this.notifyIcon1.Visible = true;
                this.notifyIcon1.ShowBalloonTip(30, rm.GetString("SYS_NoticeHead"), rm.GetString("SYS_NoticeText"), ToolTipIcon.Info);
            }
        }


        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ShowForm(); //連點兩下工具列的icon也會顯示主畫面
        }

        /// <summary>
        /// 顯示出主畫面
        /// </summary>
        private void ShowForm()
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                //如果目前是縮小狀態，才要回覆成一般大小的視窗
                this.Show();
                this.WindowState = FormWindowState.Normal;
            }
            // Activate the form.
            this.Activate();
            this.Focus();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
            CloseSelf(); //關閉自己
        }

        //關閉執行緒，關閉自己
        private void CloseSelf()
        {
            if (MessageBox.Show(rm.GetString("SYS_Exit"), rm.GetString("SYS_Info"), MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1) == DialogResult.Yes)
            {
                //關閉執行緒
                try
                {
                    othread.Abort();
                    othreadDisconnectCheck.Abort();
                }
                catch (Exception exception)
                {
                }
                Dispose();
            }

        }

        private void 打開主介面ToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (this.Visible)
            {
                this.Hide();
            }
            else
            {
                this.Show();
            }
        }

        /// <summary>
        /// 關閉程式
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (MessageBox.Show(rm.GetString("SYS_Exit"), rm.GetString("SYS_Info"), MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1) == DialogResult.Yes)
            {
                //關閉執行緒
                try
                {
                    othread.Abort();
                    othreadDisconnectCheck.Abort();
                }
                catch (Exception exception)
                {
                }
                Dispose();
                e.Cancel = false;
            }
            else
                e.Cancel = true;
        }
    }


}
