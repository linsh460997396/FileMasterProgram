using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace FileMaster
{
    public partial class Form1 : Form
    {
        #region 全局变量、常量、字段及其属性方法
        bool workStatus = false; //工作状态
        bool workStop = false; //打断工作用的状态变量
        int workErrCount = 0; //修改文件名时错误计数（无法处理的次数）
        int startIndex; //删除文件名字符时，用户填入的字符起始位置参数
        int strCount; //从文件名起始位置开始，用户填入要删除的字符数量
        int dirCount = 0;//影响首次文件夹统计时的换行
        int proCount; //进度计数
        int proCountMax; //进度最大数
        Thread th;//后台子线程
        StreamWriter sWork;
        StreamWriter sReport;

        //Field
        public ushort FOF_ALLOWUNDO { get; private set; }

        bool _userOpEnable = true;
        public bool Get_userOpEnable()
        {
            return _userOpEnable;
        }
        void Set_userOpEnable(bool value)
        {
            _userOpEnable = value;
        }
        #endregion

        #region Functions
        /// <summary>
        ///  用来遍历删除目录下的文件(夹)
        /// </summary>
        public void DeleteFileByDirectory(DirectoryInfo info)
        {
            foreach (DirectoryInfo newInfo in info.GetDirectories())
            {
                DeleteFileByDirectory(newInfo);
            }
            foreach (FileInfo newInfo in info.GetFiles())
            {
                newInfo.Attributes &= ~(FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden);
                newInfo.Delete();
            }
            info.Attributes &= ~(FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden);
            info.Delete();

        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [Flags]
        public enum SHFileOperationFlags : ushort
        {
            FOF_SILENT = 0x0004,  //不出现确认或者询问用户即执行
            FOF_NOCONFIRMATION = 0x0010,  //不出现任何对话框
            FOF_ALLOWUNDO = 0x0040,  //文件删除后可以放到回收站
            FOF_NOERRORUI = 0x0400,  //不出现错误对话框
        }

        private void DeleteFileToRecycleBin(string fileName)
        {
            if (!File.Exists(fileName))
            {
                return;
            }
            SHFILEOPSTRUCT fileop = new SHFILEOPSTRUCT
            {
                wFunc = 0x003,//删除文件到回收站
                pFrom = fileName + '\0'//多个文件以 \0 分隔
            };
            if (!checkBox35.Checked) { fileop.fFlags = (ushort)(SHFileOperationFlags.FOF_ALLOWUNDO | SHFileOperationFlags.FOF_NOCONFIRMATION); }
            else
            {
                //不确认删除，否则需要用户确认
                fileop.fFlags = 0;
            }
            SHFileOperation(ref fileop);
        }

        private void DeleteDirectoryToRecycleBin(string directoryName)
        {
            if (!Directory.Exists(directoryName))
            {
                return;
            }

            SHFILEOPSTRUCT fileop = new SHFILEOPSTRUCT
            {
                wFunc = 0x003,//删除文件到回收站
                pFrom = directoryName + '\0'//多个文件以 \0 分隔
            };
            if (!checkBox35.Checked) { fileop.fFlags = (ushort)(SHFileOperationFlags.FOF_ALLOWUNDO | SHFileOperationFlags.FOF_NOCONFIRMATION); }
            else
            {
                //不确认删除，否则需要用户确认
                fileop.fFlags = 0;
            }
            SHFileOperation(ref fileop);
        }

        /// <summary>
        /// 将字节大小转字符串Byte KB MB GB形式
        /// </summary>
        /// <param name="Size">字节大小</param>
        /// <returns></returns>
        string CountSize(long Size)
        {
            string strSize = "";
            if (checkBox39.Checked)
            {
                strSize = StringAddComma(Size.ToString()) + " Byte";
            }
            else
            {
                if (Size < 1024.00)
                    strSize = Size.ToString() + " Byte";
                else if (Size >= 1024.00 && Size < 1048576)
                    strSize = (Size / 1024.00).ToString("F2") + " KB";
                else if (Size >= 1048576 && Size < 1073741824)
                    strSize = (Size / 1024.00 / 1024.00).ToString("F2") + " MB";
                else if (Size >= 1073741824)
                    strSize = (Size / 1024.00 / 1024.00 / 1024.00).ToString("F2") + " GB";
            }
            return strSize;
        }

        string StringAddComma(string strSize)
        {
            string n = "";
            for (int i = strSize.Length - 1, l = 1; i >= 0; i--)
            {
                n = strSize[i].ToString() + n;
                if (l > 0 && i > 0 && l % 3 == 0)
                {
                    n = "," + n;
                    l = 0;
                }
                l++;
            }
            return n;
        }

        /// <summary>
        /// 获取文件大小
        /// </summary>
        /// <param name="fileFullName">文件名完整路径</param>
        /// <returns></returns>
        public static long GetFileLength(string fileFullName)
        {
            long len = 0;
            if (File.Exists(fileFullName))
                len = new FileInfo(fileFullName).Length;
            return len;
        }

        /// <summary>
        /// 获取文件夹大小（递归耗时）
        /// </summary>
        /// <param name="dirPath">文件夹完整路径</param>
        /// <returns></returns>
        public static long GetDirectoryLength(string dirPath)
        {
            //判断给定的路径是否存在,如果不存在则退出
            if (!Directory.Exists(dirPath))
                return 0;
            long len = 0;
            //定义一个DirectoryInfo对象
            DirectoryInfo di = new DirectoryInfo(dirPath);
            //通过GetFiles方法,获取di目录中的所有文件的大小，量越大越慢
            foreach (FileInfo fi in di.GetFiles())
            {
                //if (workStop) { break; }
                len += fi.Length;
            }
            //获取di中所有的文件夹,并存到一个新的对象数组中,以进行递归
            DirectoryInfo[] dis = di.GetDirectories();
            if (dis.Length > 0)
            {
                for (int i = 0; i < dis.Length; i++)
                {
                    //if (workStop) { break; }
                    len += GetDirectoryLength(dis[i].FullName);
                }
            }
            return len;
        }

        /// <summary>
        /// 取得设备硬盘的卷标号
        /// </summary>
        /// <param name="diskSymbol"></param>
        /// <returns></returns>
        public static string GetHardDiskID(string diskSymbol)
        {
            try
            {
                string hdInfo = "";
                ManagementObject disk = new ManagementObject(
                    "win32_logicaldisk.deviceid=\"" + diskSymbol + ":\""
                );
                hdInfo = disk.Properties["VolumeSerialNumber"].Value.ToString();
                disk = null;
                return hdInfo.Trim();
            }
            catch
            {
                return "uHnIk";
            }
        }

        /// <summary>
        /// 验证字符串是整数
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static bool IsNumeric(string str)
        {
            Regex reg1 = new Regex(@"^[0-9]\d*$");
            return reg1.IsMatch(str);
        }

        /// <summary>
        /// 验证字符串是否为有效文件（夹），支持虚拟路径
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        public static bool IsValidDFPath(string val)
        {
            Regex regex = new Regex(
                @"^([a-zA-Z]:\\)([-\u4e00-\u9fa5\w\s.()~!@#$%^&()\[\]{}+=]+\\?)*$"
            );
            Match result = regex.Match(val);
            return result.Success;
        }

        /// <summary>
        /// 验证目录为空
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static bool IsDirectoryEmpty(string path)
        {
            bool torf = false;
            DirectoryInfo di = new DirectoryInfo(path);
            //为了效率，只要验证当前层就可以了
            if (di.GetFiles().Length + di.GetDirectories().Length == 0)
            {
                torf = true;
            }
            return torf;
        }

        /// <summary>
        /// 验证路径是否为用户定义的空文件夹
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private bool IsDirectoryEmptyUserDef(string path)
        {
            bool torf = false;
            switch (comboBox3.SelectedIndex) //定义空文件夹形式
            {
                case 0:
                    if (IsDirectoryEmpty(path))
                    {
                        torf = true;
                    } //里面的子文件夹和文件数量均为0
                    break;
                case 1:
                    if (CountSize(GetDirectoryLength(path)) == "0 Byte")//若Byte统计函数设计有小数点，那这里也要一致
                    {
                        torf = true;
                    } //文件夹大小为0
                    break;
                case 2:
                    if (IsDirectoryEmpty(path) && CountSize(GetDirectoryLength(path)) == "0 Byte")
                    {
                        torf = true;
                    } //以上两者
                    break;
                default:
                    if (IsDirectoryEmpty(path))
                    {
                        torf = true;
                    } //里面的子文件夹和文件数量均为0
                    break;
            }
            return torf;
        }

        /// <summary>
        /// 判断目标（非虚拟路径）的属性是文件还是目录(包括磁盘)
        /// </summary>
        /// <param name="path">路径</param>
        /// <returns>返回true为一个文件夹</returns>
        public static bool IsDir(string path)
        {
            FileInfo fi = new FileInfo(path);
            if ((fi.Attributes & FileAttributes.Directory) != 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// 写文本每行，若不存在文件则新建文件
        /// </summary>
        /// <param name="path"></param>
        /// <param name="value"></param>
        /// <param name="torf">设置为true是追加文本</param>
        void WriteLine(string path, string value, bool torf)
        {
            if (!torf)
            {
                if (sWork != null) { sWork.Dispose(); }
                sWork = new StreamWriter(path, torf, Encoding.Unicode);
            }
            if (sWork == null) { sWork = new StreamWriter(path, torf, Encoding.Unicode); }
            sWork.WriteLine(value);
        }

        void WriteLine(string temp, string path, string value, bool torf)
        {
            switch (temp)
            {
                case "Report":
                    if (!torf)
                    {
                        if (sReport != null) { sReport.Dispose(); }
                        sReport = new StreamWriter(path, torf, Encoding.Unicode);
                    }
                    if (sReport == null) { sReport = new StreamWriter(path, torf, Encoding.Unicode); }
                    sReport.WriteLine(value);
                    break;
                default:
                    if (!torf)
                    {
                        if (sWork != null) { sWork.Dispose(); }
                        sWork = new StreamWriter(path, torf, Encoding.Unicode);
                    }
                    if (sWork == null) { sWork = new StreamWriter(path, torf, Encoding.Unicode); }
                    sWork.WriteLine(value);
                    break;
            }

        }

        /// <summary>
        /// 判断功能编号01所需参数文本是否都是数字
        /// </summary>
        /// <returns></returns>
        private bool ParamIsNumeric()
        {
            bool torf = true;
            if (!IsNumeric(textBox3.Text))
            {
                textBox3.Text = "请重填！";
                torf = false;
            }
            else
            {
                strCount = Convert.ToInt32(textBox3.Text);
                if (strCount < 0)
                {
                    textBox3.Text = "请重填！";
                    torf = false;
                }
            }
            if (!IsNumeric(textBox4.Text))
            {
                textBox4.Text = "请重填！";
                torf = false;
            }
            else
            {
                startIndex = Convert.ToInt32(textBox4.Text);
                if (startIndex < 0)
                {
                    textBox4.Text = "请重填！";
                    torf = false;
                }
            }
            return torf;
        }

        /// <summary>
        /// 验证检索文件大小是否在用户定义范围（递归耗时）
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private bool ParamIsInRange(string path)
        {
            bool torf = false;
            long x;
            long a;
            long b;
            for (int i = 0; i < 1; i++)
            {
                if (IsNumeric(textBox6.Text) && IsNumeric(textBox7.Text))
                {
                    a = long.Parse(textBox6.Text);
                    b = long.Parse(textBox7.Text);
                    if (IsDir(path))
                    {
                        x = GetDirectoryLength(path);
                    }
                    else
                    {
                        x = GetFileLength(path);
                    }
                    if (x >= a && x < b)
                    {
                        torf = true;
                    }
                }
            }
            return torf;
        }

        /// <summary>
        /// 首次写入文件时标记卷名卷号
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="diskSymbol">工作目录盘符</param>
        private void FirstWrite(string workFilePath, string diskSymbol)
        {
            bool torf;
            if (checkBox2.Checked)
            {
                torf = true;
            }
            else
            {
                torf = false;
            }
            DriveInfo drive = new DriveInfo(diskSymbol);
            if (drive.Name == $"{diskSymbol}" + @":\")
            {
                if (checkBox9.Checked)
                {
                    WriteLine(
                        workFilePath,
                        " Volume in drive " + diskSymbol + " is " + drive.VolumeLabel,
                        torf
                    );
                }

                if (checkBox16.Checked)
                {
                    WriteLine(
                        workFilePath,
                        " Volume Serial Number is " + GetHardDiskID(diskSymbol),
                        true
                    );
                }
                WriteLine(workFilePath, "", true);
            }

        }

        private void UserOpEnable(bool torf)
        {
            Set_userOpEnable(torf);
            foreach (Control a in Controls)
            {
                if (a is Panel)
                {
                    Panel p = a as Panel;  //取出Panel
                    if (!torf)
                    {
                        // 改变Panel的颜色，执行时是灰色
                        p.BackColor = Color.Gray;
                    }
                    else
                    {
                        //不执行时是白色
                        p.BackColor = Color.Transparent;
                    }
                    foreach (Control c in p.Controls) //遍历面板中的每一个控件
                    {
                        if (c.GetType().Name.Equals("TextBox"))
                        {
                            c.Enabled = torf;//禁用文本框
                        }
                        if (c.GetType().Name.Equals("CheckBox"))
                        {
                            c.Enabled = torf;//禁用复选框
                        }
                        if (c.GetType().Name.Equals("ComboBox"))
                        {
                            c.Enabled = torf;//禁用下拉框
                        }
                        if (c.GetType().Name.Equals("Button"))
                        {
                            if (!c.Name.Equals("button2"))
                            {
                                c.Enabled = torf;//禁用除运行按钮外的其他按钮
                            }
                        }

                    }
                }
            }
        }

        /// <summary>
        /// 点击执行按钮
        /// </summary>
        private void ButtonRun()
        {
            string workPath;
            string workFilePath;
            string diskSymbol;

            for (int i = 0; i < 1; i++)
            {
                workPath = textBox1.Text;
                workFilePath = textBox2.Text;

                switch (comboBox1.SelectedIndex)
                {
                    case 0:
                        label8.Text = "批量将文件（夹）名称打印到工作文件";
                        break;
                    case 1:
                        label8.Text = "批量将文件（夹）名称去除固定位数字符";
                        break;
                    case 2:
                        label8.Text = "批量将文件（夹）名称保留固定位数字符";
                        break;
                    case 3:
                        label8.Text = "批量插入字符到文件（夹）名称固定位数字符前";
                        break;
                    case 4:
                        label8.Text = "批量插入字符到文件（夹）名称固定位数字符后";
                        break;
                    case 5:
                        label8.Text = "批量插入字符到文件（夹）名称最前";
                        break;
                    case 6:
                        label8.Text = "批量插入字符到文件（夹）名称最后";
                        break;
                    case 7:
                        label8.Text = "批量替换文件（夹）名称固定位数字符";
                        break;
                    case 8:
                        label8.Text = "批量替换文件（夹）名称的指定字符";
                        break;
                    case 9:
                        label8.Text = "批量替换文件（夹）名称的后缀字符";
                        break;
                    case 10:
                        label8.Text = "批量删除（移动）指定名称的文件（夹）";
                        break;
                    default:
                        label8.Text = "功能未选择！";
                        break;
                }
                if ((label8.Text == "功能未选择！") || (comboBox1.SelectedIndex == -1))
                {
                    break;
                }
                if (!Directory.Exists(workPath))
                {
                    label8.Text = "工作目录无效！";
                    break; //调用本函数的动作有验证工作目录路径字符，通过后执行到此处再次验证有无此文件夹，无则打断
                }
                else
                {
                    diskSymbol = workPath.Substring(0, 1);
                }
                //生成指定文本到程序目录的情况：1）工作文本为空；2）工作文本路径已存在且未允许覆盖；3）勾选仅输出.txt但工作文本格式错误，虽不为空但后缀非.txt；4）文件路径非法。
                if (
                    workFilePath == ""
                    || (File.Exists(workFilePath) && !checkBox1.Checked)
                    || (Regex.IsMatch(workFilePath, @"^(.*)(\.txt)$") && !checkBox7.Checked)
                    || !IsValidDFPath(workFilePath)
                )
                {
                    //工作文本路径错误，重置为系统默认
                    workFilePath = AppDomain.CurrentDomain.BaseDirectory + @"temp.txt";
                    textBox2.Text = workFilePath;
                    label8.Text = "工作文本路径错误，按系统默认输出！";
                }
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                DFRun(workPath, workFilePath, diskSymbol);
                stopwatch.Stop();
                //WriteLine(workFilePath, stopwatch.Elapsed.ToString(), true);
                if (checkBox40.Checked) { label11.Text += " 时耗 => " + stopwatch.Elapsed.ToString(); }
            }
            //放弃了线程注销做法，程序将始终运行至此，可以知道是用户中断还是正常运行结束
            dirCount = 0;//影响打印的目录是否换行
            workStatus = false;//重置工作状态
            if (workStop) { label11.Text = "用户取消！"; }
            workStop = false;//重置workStop状态，如果是用户取消的，打印告知
            workErrCount = 0;//重置错误计数
            UserOpEnable(true);//重置用户操作状态
            button2.Text = "执行";
            sWork.Dispose();
            if (comboBox1.SelectedIndex !=0) { sReport.Dispose(); }
        }

        /// <summary>
        /// 按照功能编号去选择执行检索文件后的动作
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction(string workFilePath, string item)
        {
            switch (comboBox1.SelectedIndex)
            {
                case 1:
                    DAction_f1(workFilePath, item);
                    break;
                case 2:
                    DAction_f2(workFilePath, item);
                    break;
                case 3:
                    DAction_f3456(workFilePath, item);
                    break;
                case 4:
                    DAction_f3456(workFilePath, item);
                    break;
                case 5:
                    DAction_f3456(workFilePath, item);
                    break;
                case 6:
                    DAction_f3456(workFilePath, item);
                    break;
                case 7:
                    DAction_f7(workFilePath, item);
                    break;
                case 8:
                    DAction_f8(workFilePath, item);
                    break;
                case 9:
                    DAction_f9(workFilePath, item);
                    break;
                case 10:
                    DAction_f10(workFilePath, item);
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// 功能编号01，将文件（夹）名称去除固定位数字符
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction_f1(string workFilePath, string item)
        {
            string dirName;
            string itemName;
            string itemFrontName;
            string itemExtension;
            string newFileName;
            string newFilePath = "";
            string reportPath;
            bool isDir = false;
            for (int i = 0; i < 1; i++)
            {
                dirName = item.Substring(0, item.LastIndexOf("\\"));
                itemName = item.Substring(item.LastIndexOf("\\") + 1);
                if (IsDir(item))
                {
                    //itemFrontName = Path.GetDirectoryName(item);//返回的是上一级文件夹名称
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                    isDir = true;
                }
                else
                {
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                }
                itemExtension = Path.GetExtension(item);
                if (itemExtension == "")
                {
                    if (checkBox30.Checked && !isDir) { break; }//忽略空后缀文件
                    if (checkBox32.Checked && isDir) { break; }//忽略空后缀文件夹
                }
                else
                {
                    if (checkBox31.Checked && !isDir) { break; }//忽略非空后缀文件
                    if (checkBox33.Checked && isDir) { break; }//忽略非空后缀文件夹
                }
                reportPath =
                    workFilePath.Substring(0, workFilePath.LastIndexOf("\\"))
                    + @"\"
                    + Path.GetFileNameWithoutExtension(workFilePath)
                    + @"_Report.txt";
                if (ParamIsNumeric() && textBox3.Text != "" && textBox4.Text != "" && strCount > 0)
                {
                    if (itemName.Length > (strCount + startIndex)) //文件名长度足以支撑修改，8>(0+7)或(1+6)，至少留一个字符
                    {
                        if (checkBox13.Checked && textBox8.Text != "*") //指定后缀且勾选后缀保护
                        {
                            if (
                                textBox8.Text == ""
                                || (
                                    string.Compare(
                                        Path.GetExtension(item),
                                        textBox8.Text,
                                        checkBox28.Checked
                                    ) == 0
                                )
                            )
                            {
                                //勾选保护后缀时的处理
                                if (itemFrontName.Length >= (strCount + startIndex)) //前缀允许全部删除
                                {
                                    newFileName =
                                        itemFrontName.Remove(startIndex, strCount) + itemExtension;
                                    newFilePath = Path.Combine(dirName + @"\", newFileName);
                                }
                                else
                                {
                                    //预判出错文件并汇报，忽略这些文件的重命名，此时newFilePath=""
                                    if (workErrCount == 0)
                                    {
                                        WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                    }
                                    workErrCount += 1;
                                    WriteLine("Report", reportPath, "前缀字符数不足以修改:" + item, true);
                                }
                            }
                        }
                        else
                        {
                            //不保护后缀（或参数4默认填了*）
                            newFileName = itemName.Remove(startIndex, strCount);
                            newFilePath = Path.Combine(dirName + @"\", newFileName);
                        }
                        if (
                            newFilePath != ""
                            && !File.Exists(newFilePath)
                            && !Directory.Exists(newFilePath)
                        )
                        {
                            //对非空newFilePath且不会发生覆盖的文件，执行重命名
                            if (!IsDir(item))
                            {
                                FileInfo fileInfo = new FileInfo(item);
                                fileInfo.MoveTo(newFilePath);
                            }
                            else
                            {
                                DirectoryInfo directoryInfo = new DirectoryInfo(item);
                                directoryInfo.MoveTo(newFilePath);
                            }
                            WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                        }
                        else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                        {
                            //newFilePath=""，说明出现预判出错文件且汇报过，现在仅对预判重叠文件进行汇报
                            if (workErrCount == 0)
                            {
                                WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                            }
                            workErrCount += 1;
                            WriteLine("Report", reportPath, "预判到路径会重叠文件:" + item, true);
                        }
                    }
                    else
                    {
                        if (workErrCount == 0)
                        {
                            WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                        }
                        workErrCount += 1;
                        WriteLine("Report", reportPath, "文件名长度不足以扣减:" + item, true);
                    }

                    if (workErrCount > 0)
                    {
                        label8.Text = $"{workErrCount}个文件处理失败！请查看Report文件";
                    }
                    //测试↓
                    //WriteLine("Report", reportPath, "item = " + item, true);
                    //WriteLine("Report", reportPath, "dirName = " + dirName, true);
                    //WriteLine("Report", reportPath, "itemName = " + itemName, true);
                    //WriteLine("Report", reportPath, "itemFrontName = " + itemFrontName, true);
                    //WriteLine("Report", reportPath, "itemExtension = " + itemExtension, true);
                }
            }
        }

        /// <summary>
        /// 功能编号02，将文件（夹）名称保留固定位数字符
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction_f2(string workFilePath, string item)
        {
            string dirName;
            string itemName;
            string itemFrontName;
            string itemExtension;
            string newFileName;
            string newFilePath = "";
            string reportPath;
            bool isDir = false;
            for (int i = 0; i < 1; i++)
            {
                dirName = item.Substring(0, item.LastIndexOf("\\"));
                itemName = item.Substring(item.LastIndexOf("\\") + 1);
                if (IsDir(item))
                {
                    //itemFrontName = Path.GetDirectoryName(item);
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                    isDir = true;
                }
                else
                {
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                }
                itemExtension = Path.GetExtension(item);
                if (itemExtension == "")
                {
                    if (checkBox30.Checked && !isDir) { break; }//忽略空后缀文件
                    if (checkBox32.Checked && isDir) { break; }//忽略空后缀文件夹
                }
                else
                {
                    if (checkBox31.Checked && !isDir) { break; }//忽略非空后缀文件
                    if (checkBox33.Checked && isDir) { break; }//忽略非空后缀文件夹
                }
                reportPath =
                    workFilePath.Substring(0, workFilePath.LastIndexOf("\\"))
                    + @"\"
                    + Path.GetFileNameWithoutExtension(workFilePath)
                    + @"_Report.txt";
                if (ParamIsNumeric() && strCount > 0)
                {
                    if (itemName.Length > (strCount + startIndex)) //文件名（带后缀）长度足以支撑修改
                    {
                        if (checkBox13.Checked && textBox8.Text != "*") //指定后缀且勾选后缀保护
                        {
                            if (
                                textBox8.Text == ""
                                || (
                                    string.Compare(
                                        Path.GetExtension(item),
                                        textBox8.Text,
                                        checkBox28.Checked
                                    ) == 0
                                )
                            )
                            {
                                //勾选保护后缀时的处理
                                if (itemFrontName.Length >= (strCount + startIndex)) //假设ABCD.txt, 8>=(0+8)或8>=(1+7)
                                {
                                    if (startIndex == 0)
                                    {
                                        newFileName =
                                            itemFrontName.Substring(startIndex, strCount)
                                            + itemExtension;
                                    }
                                    else
                                    {
                                        newFileName =
                                            itemFrontName.Substring(startIndex, strCount)
                                            + itemExtension;
                                    }
                                    newFilePath = Path.Combine(dirName + @"\", newFileName);
                                }
                                else
                                {
                                    //预判出错文件并汇报，忽略这些文件的重命名，此时newFilePath=""
                                    if (workErrCount == 0)
                                    {
                                        WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                    }
                                    workErrCount += 1;
                                    WriteLine("Report", reportPath, "前缀字符数不足以修改:" + item, true);
                                }
                            }
                        }
                        else
                        {
                            //不保护后缀（或参数4默认填了*）
                            if (startIndex == 0)
                            {
                                newFileName = itemName.Remove(strCount);
                            }
                            else
                            {
                                newFileName = itemName.Remove(0, startIndex);
                                newFileName = newFileName.Remove(strCount - startIndex);
                            }
                            newFilePath = Path.Combine(dirName + @"\", newFileName);
                        }
                        if (
                            newFilePath != ""
                            && !File.Exists(newFilePath)
                            && !Directory.Exists(newFilePath)
                        )
                        {
                            //对非空newFilePath且不会发生覆盖的文件，执行重命名
                            if (!IsDir(item))
                            {
                                FileInfo fileInfo = new FileInfo(item);
                                fileInfo.MoveTo(newFilePath);
                            }
                            else
                            {
                                DirectoryInfo directoryInfo = new DirectoryInfo(item);
                                directoryInfo.MoveTo(newFilePath);
                            }
                            WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                        }
                        else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                        {
                            //newFilePath=""，说明出现预判出错文件且汇报过，现在仅对预判重叠文件进行汇报
                            if (workErrCount == 0)
                            {
                                WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                            }
                            workErrCount += 1;
                            WriteLine("Report", reportPath, "预判到路径会重叠文件:" + item, true);
                        }
                    }
                    else
                    {
                        if (workErrCount == 0)
                        {
                            WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                        }
                        workErrCount += 1;
                        WriteLine("Report", reportPath, "文件名长度不足以扣减:" + item, true);
                    }

                    if (workErrCount > 0)
                    {
                        label8.Text = $"{workErrCount}个文件处理失败！请查看Report文件";
                    }
                    //测试↓
                    //WriteLine("Report", reportPath, "item = "+ item, true);
                    //WriteLine("Report", reportPath, "dirName = " + dirName, true);
                    //WriteLine("Report", reportPath, "itemName = " + itemName, true);
                    //WriteLine("Report", reportPath, "itemFrontName = " + itemFrontName, true);
                    //WriteLine("Report", reportPath, "itemExtension = " + itemExtension, true);
                }
            }
        }

        /// <summary>
        /// 功能编号03~06，插入字符到文件（夹）名称固定位数字符前后或名称最前最后
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction_f3456(string workFilePath, string item)
        {
            string dirName;
            string itemName;
            string itemFrontName;
            string itemExtension;
            string newFileName = "";
            string newFilePath = "";
            string reportPath;
            string tempFrontName;
            string tempMidName;
            string tempEndName;
            bool torf = false;
            bool isDir = false;
            //固定功能5和6的参数
            switch (comboBox1.SelectedIndex)
            {
                case 5:
                    startIndex = 0;
                    strCount = 1;
                    torf = true;
                    break;
                case 6:
                    startIndex = 0;
                    strCount = 1;
                    torf = true;
                    break;
                default:
                    break;
            }

            for (int i = 0; i < 1; i++)
            {
                dirName = item.Substring(0, item.LastIndexOf("\\"));
                itemName = item.Substring(item.LastIndexOf("\\") + 1);
                if (IsDir(item))
                {
                    //itemFrontName = Path.GetDirectoryName(item); //返回的是上一级文件夹名称
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                    isDir = true;
                }
                else
                {
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                }
                itemExtension = Path.GetExtension(item);
                if (itemExtension == "")
                {
                    if (checkBox30.Checked && !isDir) { break; }//忽略空后缀文件
                    if (checkBox32.Checked && isDir) { break; }//忽略空后缀文件夹
                }
                else
                {
                    if (checkBox31.Checked && !isDir) { break; }//忽略非空后缀文件
                    if (checkBox33.Checked && isDir) { break; }//忽略非空后缀文件夹
                }
                reportPath =
                    workFilePath.Substring(0, workFilePath.LastIndexOf("\\"))
                    + @"\"
                    + Path.GetFileNameWithoutExtension(workFilePath)
                    + @"_Report.txt";
                if ((torf || ParamIsNumeric()) && strCount > 0)
                {
                    if (itemName.Length >= (strCount + startIndex)) //文件名（带后缀）长度足以支撑修改
                    {
                        if (checkBox13.Checked && textBox8.Text != "*") //指定后缀且勾选后缀保护
                        {
                            if (
                                textBox8.Text == ""
                                || (
                                    string.Compare(
                                        Path.GetExtension(item),
                                        textBox8.Text,
                                        checkBox28.Checked
                                    ) == 0
                                )
                            )
                            {
                                //勾选保护后缀时的处理
                                if (itemFrontName.Length >= (strCount + startIndex)) //假设ABCD.txt, 8>=(0+8)或8>=(1+7)
                                {
                                    if (startIndex == 0)
                                    {
                                        tempFrontName = "";
                                        tempMidName = itemFrontName.Substring(0, strCount);
                                        if (itemFrontName.Length == strCount)
                                        {
                                            tempEndName = "";
                                        }
                                        else
                                        {
                                            tempEndName = itemFrontName.Substring(strCount);
                                        }
                                    }
                                    else
                                    {
                                        tempFrontName = itemFrontName.Substring(0, startIndex);
                                        tempMidName = itemFrontName.Substring(startIndex, strCount);
                                        if (itemFrontName.Length > (strCount + startIndex))
                                        {
                                            //字符数有余，如8>(1+6)的情况
                                            tempEndName = itemFrontName.Substring(strCount + 1);
                                        }
                                        else
                                        {
                                            //字符数正好，如8=(1+7)的情况
                                            tempEndName = "";
                                        }
                                    }
                                    switch (comboBox1.SelectedIndex)
                                    {
                                        case 3:
                                            newFileName =
                                                tempFrontName
                                                + textBox9.Text
                                                + tempMidName
                                                + tempEndName
                                                + itemExtension;
                                            break;
                                        case 4:
                                            newFileName =
                                                tempFrontName
                                                + tempMidName
                                                + textBox9.Text
                                                + tempEndName
                                                + itemExtension;
                                            break;
                                        case 5:
                                            newFileName =
                                                textBox9.Text
                                                + tempFrontName
                                                + tempMidName
                                                + tempEndName
                                                + itemExtension;
                                            break;
                                        case 6:
                                            newFileName =
                                                tempFrontName
                                                + tempMidName
                                                + tempEndName
                                                + textBox9.Text
                                                + itemExtension;
                                            break;
                                        default:
                                            break;
                                    }
                                    newFilePath = Path.Combine(dirName + @"\", newFileName);
                                }
                                else
                                {
                                    //预判出错文件并汇报，忽略这些文件的重命名，此时newFilePath=""
                                    if (workErrCount == 0)
                                    {
                                        WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                    }
                                    workErrCount += 1;
                                    WriteLine("Report", reportPath, "固定字符数超过前缀字符数:" + item, true);
                                }
                            }
                        }
                        else
                        {
                            //不保护后缀（或参数4默认填了*）
                            if (startIndex == 0)
                            {
                                tempFrontName = "";
                                tempMidName = itemName.Substring(0, strCount);
                                if (itemName.Length == strCount)
                                {
                                    tempEndName = "";
                                }
                                else
                                {
                                    tempEndName = itemName.Substring(strCount);
                                }
                            }
                            else
                            {
                                tempFrontName = itemName.Substring(0, startIndex);
                                tempMidName = itemName.Substring(startIndex, strCount);
                                if (itemName.Length > (strCount + startIndex))
                                {
                                    //字符数有余，如8>(1+6)的情况
                                    tempEndName = itemName.Substring(strCount + 1);
                                }
                                else
                                {
                                    //字符数正好，如8=(1+7)的情况
                                    tempEndName = "";
                                }
                            }
                            switch (comboBox1.SelectedIndex)
                            {
                                case 3:
                                    newFileName =
                                        tempFrontName + textBox9.Text + tempMidName + tempEndName;
                                    break;
                                case 4:
                                    newFileName =
                                        tempFrontName + tempMidName + textBox9.Text + tempEndName;
                                    break;
                                case 5:
                                    newFileName =
                                        textBox9.Text + tempFrontName + tempMidName + tempEndName;
                                    break;
                                case 6:
                                    newFileName =
                                        tempFrontName + tempMidName + tempEndName + textBox9.Text;
                                    break;
                                default:
                                    break;
                            }
                            newFilePath = Path.Combine(dirName + @"\", newFileName);
                        }
                        if (
                            newFilePath != ""
                            && !File.Exists(newFilePath)
                            && !Directory.Exists(newFilePath)
                        )
                        {
                            //对非空newFilePath且不会发生覆盖的文件，执行重命名
                            if (!IsDir(item))
                            {
                                FileInfo fileInfo = new FileInfo(item);
                                fileInfo.MoveTo(newFilePath);
                            }
                            else
                            {
                                DirectoryInfo directoryInfo = new DirectoryInfo(item);
                                directoryInfo.MoveTo(newFilePath);
                            }
                            WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                        }
                        else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                        {
                            //newFilePath=""，说明出现预判出错文件且汇报过，现在仅对预判重叠文件进行汇报
                            if (workErrCount == 0)
                            {
                                WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                            }
                            workErrCount += 1;
                            WriteLine("Report", reportPath, "预判到路径会重叠文件:" + item, true);
                        }
                    }
                    else
                    {
                        if (workErrCount == 0)
                        {
                            WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                        }
                        workErrCount += 1;
                        WriteLine("Report", reportPath, "固定字符数超过对象字符数:" + item, true);
                    }

                    if (workErrCount > 0)
                    {
                        label8.Text = $"{workErrCount}个文件处理失败！请查看Report文件";
                    }
                    //测试↓
                    //WriteLine("Report", reportPath, "item = " + item, true);
                    //WriteLine("Report", reportPath, "dirName = " + dirName, true);
                    //WriteLine("Report", reportPath, "itemName = " + itemName, true);
                    //WriteLine("Report", reportPath, "itemFrontName = " + itemFrontName, true);
                    //WriteLine("Report", reportPath, "itemExtension = " + itemExtension, true);
                    //WriteLine("Report", reportPath, "tempFrontName = " + tempFrontName, true);
                    //WriteLine("Report", reportPath, "tempMidName = " + tempMidName, true);
                    //WriteLine("Report", reportPath, "tempEndName = " + tempEndName, true);
                }
            }
        }

        /// <summary>
        /// 功能编号07，批量替换文件（夹）名称固定位数字符
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction_f7(string workFilePath, string item)
        {
            string dirName;
            string itemName;
            string itemFrontName;
            string itemExtension;
            string newFileName;
            string newFilePath = "";
            string reportPath;
            string tempFrontName;
            //string tempMidName;
            string tempEndName;
            bool torf = false;
            bool isDir = false;
            for (int i = 0; i < 1; i++)
            {
                dirName = item.Substring(0, item.LastIndexOf("\\"));
                itemName = item.Substring(item.LastIndexOf("\\") + 1);
                if (IsDir(item))
                {
                    //itemFrontName = Path.GetDirectoryName(item); //返回的是上一级文件夹名称
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                    isDir = true;
                }
                else
                {
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                }
                itemExtension = Path.GetExtension(item);
                if (itemExtension == "")
                {
                    if (checkBox30.Checked && !isDir) { break; }//忽略空后缀文件
                    if (checkBox32.Checked && isDir) { break; }//忽略空后缀文件夹
                }
                else
                {
                    if (checkBox31.Checked && !isDir) { break; }//忽略非空后缀文件
                    if (checkBox33.Checked && isDir) { break; }//忽略非空后缀文件夹
                }
                reportPath =
                    workFilePath.Substring(0, workFilePath.LastIndexOf("\\"))
                    + @"\"
                    + Path.GetFileNameWithoutExtension(workFilePath)
                    + @"_Report.txt";
                if (ParamIsNumeric() && strCount > 0)
                {
                    if (textBox9.Text == "" && (itemName.Length > (strCount + startIndex)))
                    {
                        torf = true;
                    }
                    else if (textBox9.Text != "" && (itemName.Length >= (strCount + startIndex)))
                    {
                        torf = true;
                    }
                    if (torf) //文件名（带后缀）长度足以支撑修改
                    {
                        if (checkBox13.Checked && textBox8.Text != "*") //指定后缀且勾选后缀保护
                        {
                            if (
                                textBox8.Text == ""
                                || (
                                    string.Compare(
                                        Path.GetExtension(item),
                                        textBox8.Text,
                                        checkBox28.Checked
                                    ) == 0
                                )
                            )
                            {
                                //勾选保护后缀时的处理
                                if (itemFrontName.Length >= (strCount + startIndex)) //假设ABCD.txt, 8>=(0+8)或8>=(1+7)
                                {
                                    if (startIndex == 0)
                                    {
                                        tempFrontName = "";
                                        //tempMidName = itemFrontName.Substring(0, strCount);
                                        if (itemFrontName.Length == strCount)
                                        {
                                            tempEndName = "";
                                        }
                                        else
                                        {
                                            tempEndName = itemFrontName.Substring(strCount);
                                        }
                                    }
                                    else
                                    {
                                        tempFrontName = itemFrontName.Substring(0, startIndex);
                                        //tempMidName = itemFrontName.Substring(startIndex, strCount);
                                        if (itemFrontName.Length > (strCount + startIndex))
                                        {
                                            //字符数有余，如8>(1+6)的情况
                                            tempEndName = itemFrontName.Substring(strCount + 1);
                                        }
                                        else
                                        {
                                            //字符数正好，如8=(1+7)的情况
                                            tempEndName = "";
                                        }
                                    }
                                    newFileName =
                                        tempFrontName + textBox9.Text + tempEndName + itemExtension;
                                    newFilePath = Path.Combine(dirName + @"\", newFileName);
                                }
                                else
                                {
                                    //预判出错文件并汇报，忽略这些文件的重命名，此时newFilePath=""
                                    if (workErrCount == 0)
                                    {
                                        WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                    }
                                    workErrCount += 1;
                                    WriteLine("Report", reportPath, "固定字符数超过前缀字符数:" + item, true);
                                }
                            }
                        }
                        else
                        {
                            //不保护后缀（或参数4默认填了*）
                            if (startIndex == 0)
                            {
                                tempFrontName = "";
                                //tempMidName = itemName.Substring(0, strCount);
                                if (itemName.Length == strCount)
                                {
                                    tempEndName = "";
                                }
                                else
                                {
                                    tempEndName = itemName.Substring(strCount);
                                }
                            }
                            else
                            {
                                tempFrontName = itemName.Substring(0, startIndex);
                                //tempMidName = itemName.Substring(startIndex, strCount);
                                if (itemName.Length > (strCount + startIndex))
                                {
                                    //字符数有余，如8>(1+6)的情况
                                    tempEndName = itemName.Substring(strCount + 1);
                                }
                                else
                                {
                                    //字符数正好，如8=(1+7)的情况
                                    tempEndName = "";
                                }
                            }
                            newFileName = tempFrontName + textBox9.Text + tempEndName;
                            newFilePath = Path.Combine(dirName + @"\", newFileName);
                        }
                        if (
                            newFilePath != ""
                            && !File.Exists(newFilePath)
                            && !Directory.Exists(newFilePath)
                        )
                        {
                            //对非空newFilePath且不会发生覆盖的文件，执行重命名
                            if (!IsDir(item))
                            {
                                FileInfo fileInfo = new FileInfo(item);
                                fileInfo.MoveTo(newFilePath);
                            }
                            else
                            {
                                DirectoryInfo directoryInfo = new DirectoryInfo(item);
                                directoryInfo.MoveTo(newFilePath);
                            }
                            WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                        }
                        else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                        {
                            //newFilePath=""，说明出现预判出错文件且汇报过，现在仅对预判重叠文件进行汇报
                            if (workErrCount == 0)
                            {
                                WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                            }
                            workErrCount += 1;
                            WriteLine("Report", reportPath, "预判到路径会重叠文件:" + item, true);
                        }
                    }
                    else
                    {
                        if (workErrCount == 0)
                        {
                            WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                        }
                        workErrCount += 1;
                        WriteLine("Report", reportPath, "固定字符数超过对象字符数:" + item, true);
                    }

                    if (workErrCount > 0)
                    {
                        label8.Text = $"{workErrCount}个文件处理失败！请查看Report文件";
                    }
                    //测试↓
                    //WriteLine("Report", reportPath, "item = " + item, true);
                    //WriteLine("Report", reportPath, "dirName = " + dirName, true);
                    //WriteLine("Report", reportPath, "itemName = " + itemName, true);
                    //WriteLine("Report", reportPath, "itemFrontName = " + itemFrontName, true);
                    //WriteLine("Report", reportPath, "itemExtension = " + itemExtension, true);
                    //WriteLine("Report", reportPath, "tempFrontName = " + tempFrontName, true);
                    //WriteLine("Report", reportPath, "tempMidName = " + tempMidName, true);
                    //WriteLine("Report", reportPath, "tempEndName = " + tempEndName, true);
                }
            }
        }

        /// <summary>
        /// 功能编号08，批量替换文件（夹）名称的指定字符
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction_f8(string workFilePath, string item)
        {
            string dirName;
            string itemName;
            string itemFrontName;
            string itemExtension;
            string newFileName;
            string newFilePath = "";
            string reportPath;
            //string tempFrontName;
            //string tempMidName;
            //string tempEndName;
            bool torf = false;
            bool isDir = false;
            //固定功能5和6的参数
            startIndex = 0;
            strCount = 1;

            for (int i = 0; i < 1; i++)
            {
                dirName = item.Substring(0, item.LastIndexOf("\\"));
                itemName = item.Substring(item.LastIndexOf("\\") + 1);
                if (IsDir(item))
                {
                    //itemFrontName = Path.GetDirectoryName(item); //返回的是上一级文件夹名称
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                    isDir = true;
                }
                else
                {
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                }
                itemExtension = Path.GetExtension(item);

                if (itemExtension == "")
                {
                    if (checkBox30.Checked && !isDir) { break; }//忽略空后缀文件
                    if (checkBox32.Checked && isDir) { break; }//忽略空后缀文件夹
                }
                else
                {
                    if (checkBox31.Checked && !isDir) { break; }//忽略非空后缀文件
                    if (checkBox33.Checked && isDir) { break; }//忽略非空后缀文件夹
                }

                reportPath =
                    workFilePath.Substring(0, workFilePath.LastIndexOf("\\"))
                    + @"\"
                    + Path.GetFileNameWithoutExtension(workFilePath)
                    + @"_Report.txt";

                if (textBox9.Text == "" && (itemName.Length > (strCount + startIndex)))
                {
                    torf = true;
                }
                else if (textBox9.Text != "" && (itemName.Length >= (strCount + startIndex)))
                {
                    torf = true;
                }
                if (torf) //文件名（带后缀）长度足以支撑修改
                {
                    if (checkBox13.Checked && textBox8.Text != "*") //指定后缀且勾选后缀保护
                    {
                        //如填了空格全保护或指定.txt后遇到.txt情况则不操作
                        //若指定.txt后遇到.PNG情况，进行操作
                        if (
                            string.Compare(
                                Path.GetExtension(item),
                                textBox8.Text,
                                checkBox28.Checked
                            ) != 0
                        )
                        {
                            //处理
                            if (itemFrontName.Length >= (strCount + startIndex)) //假设ABCD.txt, 8>=(0+8)或8>=(1+7)
                            {
                                //if (startIndex == 0)
                                //{
                                //    tempFrontName = "";
                                //    tempMidName = itemFrontName.Substring(0, strCount);
                                //    if (itemFrontName.Length == strCount)
                                //    {
                                //        tempEndName = "";
                                //    }
                                //    else
                                //    {
                                //        tempEndName = itemFrontName.Substring(strCount);
                                //    }
                                //}
                                //else
                                //{
                                //    tempFrontName = itemFrontName.Substring(0, startIndex);
                                //    tempMidName = itemFrontName.Substring(startIndex, strCount);
                                //    if (itemFrontName.Length > (strCount + startIndex))
                                //    {
                                //        //字符数有余，如8>(1+6)的情况
                                //        tempEndName = itemFrontName.Substring(strCount + 1);
                                //    }
                                //    else
                                //    {
                                //        //字符数正好，如8=(1+7)的情况
                                //        tempEndName = "";
                                //    }
                                //}
                                newFileName = itemFrontName.Replace(textBox9.Text, textBox10.Text) + itemExtension;
                                newFilePath = Path.Combine(dirName + @"\", newFileName);
                            }
                            else
                            {
                                //预判出错文件并汇报，忽略这些文件的重命名，此时newFilePath=""
                                if (workErrCount == 0)
                                {
                                    WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                }
                                workErrCount += 1;
                                WriteLine("Report", reportPath, "固定字符数超过前缀字符数:" + item, true);
                            }
                        }
                    }
                    else
                    {
                        //不保护后缀（或参数4默认填了*）
                        newFileName = itemName.Replace(textBox9.Text, textBox10.Text);
                        newFilePath = Path.Combine(dirName + @"\", newFileName);
                    }
                    if (
                        newFilePath != ""
                        && !File.Exists(newFilePath)
                        && !Directory.Exists(newFilePath)
                    )
                    {
                        //对非空newFilePath且不会发生覆盖的文件，执行重命名
                        if (!IsDir(item))
                        {
                            FileInfo fileInfo = new FileInfo(item);
                            fileInfo.MoveTo(newFilePath);
                        }
                        else
                        {
                            DirectoryInfo directoryInfo = new DirectoryInfo(item);
                            directoryInfo.MoveTo(newFilePath);
                        }
                        WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                    }
                    else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                    {
                        //newFilePath=""，说明出现预判出错文件且汇报过，现在仅对预判重叠文件进行汇报
                        if (workErrCount == 0)
                        {
                            WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                        }
                        workErrCount += 1;
                        WriteLine("Report", reportPath, "预判到路径会重叠文件:" + item, true);
                    }
                }
                else
                {
                    if (workErrCount == 0)
                    {
                        WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                    }
                    workErrCount += 1;
                    WriteLine("Report", reportPath, "固定字符数超过对象字符数:" + item, true);
                }

                if (workErrCount > 0)
                {
                    label8.Text = $"{workErrCount}个文件处理失败！请查看Report文件";
                }
                //测试↓
                //WriteLine("Report", reportPath, "item = " + item, true);
                //WriteLine("Report", reportPath, "dirName = " + dirName, true);
                //WriteLine("Report", reportPath, "itemName = " + itemName, true);
                //WriteLine("Report", reportPath, "itemFrontName = " + itemFrontName, true);
                //WriteLine("Report", reportPath, "itemExtension = " + itemExtension, true);
                //WriteLine("Report", reportPath, "tempFrontName = " + tempFrontName, true);
                //WriteLine("Report", reportPath, "tempMidName = " + tempMidName, true);
                //WriteLine("Report", reportPath, "tempEndName = " + tempEndName, true);
            }
        }

        /// <summary>
        /// 功能编号09，批量替换文件（夹）名称的后缀字符
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction_f9(string workFilePath, string item)
        {
            string dirName;
            string itemName;
            string itemFrontName;
            string itemExtension;
            string newFileName;
            string newFilePath = "";
            string reportPath;
            string tempFrontName;
            string tempMidName;
            string tempEndName;
            bool torf = false;
            bool isDir = false;
            //固定功能5和6的参数
            startIndex = 0;
            strCount = 1;

            for (int i = 0; i < 1; i++)
            {
                dirName = item.Substring(0, item.LastIndexOf("\\"));
                itemName = item.Substring(item.LastIndexOf("\\") + 1);
                if (IsDir(item))
                {
                    //itemFrontName = Path.GetDirectoryName(item); //返回的是上一级文件夹名称
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                    isDir = true;
                }
                else
                {
                    itemFrontName = Path.GetFileNameWithoutExtension(item);
                }
                itemExtension = Path.GetExtension(item);

                if (itemExtension == "")
                {
                    if (checkBox30.Checked && !isDir) { break; }//忽略空后缀文件
                    if (checkBox32.Checked && isDir) { break; }//忽略空后缀文件夹
                }
                else
                {
                    if (checkBox31.Checked && !isDir) { break; }//忽略非空后缀文件
                    if (checkBox33.Checked && isDir) { break; }//忽略非空后缀文件夹
                }

                reportPath =
                    workFilePath.Substring(0, workFilePath.LastIndexOf("\\"))
                    + @"\"
                    + Path.GetFileNameWithoutExtension(workFilePath)
                    + @"_Report.txt";

                if (textBox9.Text == "" && (itemName.Length > (strCount + startIndex)))
                {
                    torf = true;
                }
                else if (textBox9.Text != "" && (itemName.Length >= (strCount + startIndex)))
                {
                    torf = true;
                }
                if (torf) //文件名（带后缀）长度足以支撑修改
                {
                    if (checkBox13.Checked && textBox8.Text != "*") //指定后缀且勾选后缀保护
                    {
                        //如填了空格全保护或指定.txt后遇到.txt情况则不操作
                        //若指定.txt后遇到.PNG情况，进行操作
                        if (
                            string.Compare(
                                Path.GetExtension(item),
                                textBox8.Text,
                                checkBox28.Checked
                            ) != 0
                        )
                        {
                            //处理
                            if (itemFrontName.Length >= (strCount + startIndex)) //假设ABCD.txt, 8>=(0+8)或8>=(1+7)
                            {
                                if (startIndex == 0)
                                {
                                    tempFrontName = "";
                                    tempMidName = itemFrontName.Substring(0, strCount);
                                    if (itemFrontName.Length == strCount)
                                    {
                                        tempEndName = "";
                                    }
                                    else
                                    {
                                        tempEndName = itemFrontName.Substring(strCount);
                                    }
                                }
                                else
                                {
                                    tempFrontName = itemFrontName.Substring(0, startIndex);
                                    tempMidName = itemFrontName.Substring(startIndex, strCount);
                                    if (itemFrontName.Length > (strCount + startIndex))
                                    {
                                        //字符数有余，如8>(1+6)的情况
                                        tempEndName = itemFrontName.Substring(strCount + 1);
                                    }
                                    else
                                    {
                                        //字符数正好，如8=(1+7)的情况
                                        tempEndName = "";
                                    }
                                }
                                newFileName =
                                    tempFrontName + tempMidName + tempEndName + textBox9.Text;
                                newFilePath = Path.Combine(dirName + @"\", newFileName);
                            }
                            else
                            {
                                //预判出错文件并汇报，忽略这些文件的重命名，此时newFilePath=""
                                if (workErrCount == 0)
                                {
                                    WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                }
                                workErrCount += 1;
                                WriteLine("Report", reportPath, "固定字符数超过前缀字符数:" + item, true);
                            }
                        }
                    }
                    else
                    {
                        //不保护后缀（或参数4默认填了*）
                        newFileName = itemFrontName + textBox9.Text;
                        newFilePath = Path.Combine(dirName + @"\", newFileName);
                    }
                    if (
                        newFilePath != ""
                        && !File.Exists(newFilePath)
                        && !Directory.Exists(newFilePath)
                    )
                    {
                        //对非空newFilePath且不会发生覆盖的文件，执行重命名
                        if (!IsDir(item))
                        {
                            FileInfo fileInfo = new FileInfo(item);
                            fileInfo.MoveTo(newFilePath);
                        }
                        else
                        {
                            DirectoryInfo directoryInfo = new DirectoryInfo(item);
                            directoryInfo.MoveTo(newFilePath);
                        }
                        WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                    }
                    else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                    {
                        //newFilePath=""，说明出现预判出错文件且汇报过，现在仅对预判重叠文件进行汇报
                        if (workErrCount == 0)
                        {
                            WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                        }
                        workErrCount += 1;
                        WriteLine("Report", reportPath, "预判到路径会重叠文件:" + item, true);
                    }
                }
                else
                {
                    if (workErrCount == 0)
                    {
                        WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                    }
                    workErrCount += 1;
                    WriteLine("Report", reportPath, "固定字符数超过对象字符数:" + item, true);
                }

                if (workErrCount > 0)
                {
                    label8.Text = $"{workErrCount}个文件处理失败！请查看Report文件";
                }
                //测试↓
                //WriteLine("Report", reportPath, "item = " + item, true);
                //WriteLine("Report", reportPath, "dirName = " + dirName, true);
                //WriteLine("Report", reportPath, "itemName = " + itemName, true);
                //WriteLine("Report", reportPath, "itemFrontName = " + itemFrontName, true);
                //WriteLine("Report", reportPath, "itemExtension = " + itemExtension, true);
                //WriteLine("Report", reportPath, "tempFrontName = " + tempFrontName, true);
                //WriteLine("Report", reportPath, "tempMidName = " + tempMidName, true);
                //WriteLine("Report", reportPath, "tempEndName = " + tempEndName, true);
            }
        }

        /// <summary>
        /// 功能编号10，批量删除（移动）指定名称的文件（夹）
        /// </summary>
        /// <param name="workFilePath"></param>
        /// <param name="item"></param>
        private void DAction_f10(string workFilePath, string item)
        {
            string dirName;
            string itemName;
            //string itemFrontName;
            string itemExtension;
            string newFileName;
            string newFilePath = "";
            string reportPath;
            bool isDir = false;
            FileInfo fileInfo;
            DirectoryInfo directoryInfo;

            for (int i = 0; i < 1; i++)
            {
                //dirName = item.Substring(0, item.LastIndexOf("\\"));
                itemName = item.Substring(item.LastIndexOf("\\") + 1);
                if (IsDir(item))
                {
                    //itemFrontName = Path.GetDirectoryName(item); //返回的是上一级文件夹名称
                    //itemFrontName = Path.GetFileNameWithoutExtension(item);
                    isDir = true;
                }
                else
                {
                    //itemFrontName = Path.GetFileNameWithoutExtension(item);
                }
                itemExtension = Path.GetExtension(item);

                if (itemExtension == "")
                {
                    if (checkBox30.Checked && !isDir) { break; }//忽略空后缀文件
                    if (checkBox32.Checked && isDir) { break; }//忽略空后缀文件夹
                }
                else
                {
                    if (checkBox31.Checked && !isDir) { break; }//忽略非空后缀文件
                    if (checkBox33.Checked && isDir) { break; }//忽略非空后缀文件夹
                }

                reportPath =
                    workFilePath.Substring(0, workFilePath.LastIndexOf("\\"))
                    + @"\"
                    + Path.GetFileNameWithoutExtension(workFilePath)
                    + @"_Report.txt";



                if (checkBox13.Checked && textBox8.Text != "*") //指定后缀且勾选后缀保护
                {
                    //如填了空格全保护或指定.txt后遇到.txt情况则不操作
                    //若指定.txt后遇到.PNG情况，进行操作
                    if (
                        string.Compare(
                            Path.GetExtension(item),
                            textBox8.Text,
                            checkBox28.Checked
                        ) != 0
                    )
                    {
                        newFileName = itemName;
                    }
                    else
                    {
                        //如指定.txt后遇到.txt情况，输出空，然后报告过滤情况，此时newFilePath=""
                        if (workErrCount == 0)
                        {
                            WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                        }
                        workErrCount += 1;
                        WriteLine("Report", reportPath, "用户保护:" + item, true);
                        break;
                    }
                }
                else
                {
                    //不保护后缀（或参数4默认填了*）
                    newFileName = itemName;
                }
                //用户自定目录的验证和替换
                if (comboBox4.SelectedIndex == 0)
                {
                    if (IsValidDFPath(textBox11.Text) && IsDir(textBox11.Text))
                    {
                        dirName = textBox11.Text;
                    }
                    else
                    {
                        //无效时，按默认输出
                        dirName = workFilePath.Substring(0, workFilePath.LastIndexOf("\\")) + @"\" + Path.GetFileNameWithoutExtension(workFilePath) + @"_Delete";
                        textBox11.Text = dirName;
                    }
                    newFilePath = Path.Combine(dirName + @"\", newFileName);
                    if (!Directory.Exists(dirName)) { Directory.CreateDirectory(dirName); }
                }

                if (!IsDir(item))
                {
                    switch (comboBox4.SelectedIndex)
                    {
                        case 0:
                            if (!File.Exists(newFilePath) && !Directory.Exists(newFilePath))
                            {
                                //新路径文件必须不存在，才可以移入
                                fileInfo = new FileInfo(item);
                                fileInfo.MoveTo(newFilePath);
                                WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                            }
                            else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                            {
                                if (workErrCount == 0)
                                {
                                    WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                }
                                workErrCount += 1;
                                WriteLine("Report", reportPath, "预判到路径会重叠文件:" + item, true);
                            }
                            break;
                        case 1:
                            DeleteFileToRecycleBin(item);
                            break;
                        case 2:
                            fileInfo = new FileInfo(item);
                            fileInfo.Delete();//不能删除只读文件
                            break;
                        default:
                            break;
                    }
                }
                else
                {
                    switch (comboBox4.SelectedIndex)
                    {
                        case 0:
                            if (!File.Exists(newFilePath) && !Directory.Exists(newFilePath))
                            {
                                //新路径文件夹必须不存在，才可以移入
                                fileInfo = new FileInfo(item);
                                fileInfo.MoveTo(newFilePath);
                                WriteLine(workFilePath, "【处理完成】" + item + " => " + newFilePath, true);
                            }
                            else if (File.Exists(newFilePath) || Directory.Exists(newFilePath))
                            {
                                if (workErrCount == 0)
                                {
                                    WriteLine("Report", reportPath, "↓未处理文件如下↓", false);
                                }
                                workErrCount += 1;
                                WriteLine("Report", reportPath, "预判到路径会重叠文件夹:" + item, true);
                            }
                            break;
                        case 1:
                            DeleteDirectoryToRecycleBin(item);
                            break;
                        case 2:
                            directoryInfo = new DirectoryInfo(item);
                            DeleteFileByDirectory(directoryInfo);
                            break;
                        default:
                            break;
                    }
                }
                if (workErrCount > 0)
                {
                    label8.Text = $"{workErrCount}个文件处理失败！请查看Report文件";
                }
                //测试↓
                //WriteLine("Report", reportPath, "item = " + item, true);
                //WriteLine("Report", reportPath, "dirName = " + dirName, true);
                //WriteLine("Report", reportPath, "itemName = " + itemName, true);
                //WriteLine("Report", reportPath, "itemFrontName = " + itemFrontName, true);
                //WriteLine("Report", reportPath, "itemExtension = " + itemExtension, true);
                //WriteLine("Report", reportPath, "tempFrontName = " + tempFrontName, true);
                //WriteLine("Report", reportPath, "tempMidName = " + tempMidName, true);
                //WriteLine("Report", reportPath, "tempEndName = " + tempEndName, true);
            }
        }

        /// <summary>
        /// 检索文件和文件夹并打印或修改
        /// </summary>
        /// <param name="workPath"></param>
        /// <param name="workFilePath"></param>
        /// <param name="diskSymbol"></param>
        private void DFRun(string workPath, string workFilePath, string diskSymbol)
        {
            string[] directories;
            for (int i = 0; i < 1; i++)
            {
                label11.Text = "任务执行中...点击此处可查看进度百分比";
                proCount = 0;

                FirstWrite(workFilePath, diskSymbol); //首次向工作文本写入信息
                if (checkBox5.Checked) //勾选文件夹与文件分列
                {
                    if (checkBox6.Checked) //勾选优先检索文件夹
                    {
                        DirRun(workPath, workFilePath);
                        WriteLine(workFilePath, "", true);
                        FileRun(workPath, workFilePath);
                    }
                    else //否则优先检索文件
                    {
                        FileRun(workPath, workFilePath);
                        WriteLine(workFilePath, "", true);
                        DirRun(workPath, workFilePath);
                    }
                }
                else
                {
                    DFPrint(workPath, workFilePath);
                    if (checkBox10.Checked) //遍历子文件夹
                    {
                        directories = Directory.GetDirectories(workPath, "*", SearchOption.AllDirectories);
                        proCountMax = directories.Length;
                        foreach (var item in directories) //处理每个遍历到的文件夹
                        {
                            if (workStop) { break; }
                            proCount++;
                            DFPrint(item, workFilePath);
                            if (workStatus)
                            {
                                label11.Text = proCount.ToString() + @"/" + proCountMax.ToString();
                            }
                        }
                    }
                }

                WriteLine(workFilePath, "████████████████████████████████████████████" + "\r\n" + "", true);//尾行留空
            }
            if (!workStop) { label11.Text = "任务已完成！"; }
        }

        /// <summary>
        /// 决定是否优先检索文件夹的函数
        /// </summary>
        /// <param name="path"></param>
        /// <param name="workFilePath"></param>
        private void DFPrint(string path, string workFilePath)
        {
            if (comboBox1.SelectedIndex == 0 && checkBox6.Checked) //仅检索名称且勾选优先检索文件夹
            {
                DPrint(path, workFilePath);
                FPrint(path, workFilePath);
            }
            else //否则始终优先检索文件（修改文件的功能需要优先处理文件）
            {
                FPrint(path, workFilePath);
                DPrint(path, workFilePath); //当把文件修改好之后，对文件夹递归修改处理（父目录改完改一层子目录）
            }
        }

        /// <summary>
        /// 检索文件夹并按用户功能动作（不含子文件夹遍历）
        /// </summary>
        /// <param name="path"></param>
        /// <param name="workFilePath"></param>
        private void DPrint(string path, string workFilePath)
        {
            string[] directories;
            string tempStr = "";
            string dirName;
            string dirSize = "";
            string dirTime = "";
            string a = "";
            string b = "";
            int j = 0;
            int k = 0;
            DirectoryInfo directoryInfo;
            if (checkBox4.Checked) //允许检索文件夹
            {
                for (int i = 0; i < 1; i++)
                {
                    if (checkBox14.Checked && IsDirectoryEmptyUserDef(path))
                    {
                        //WriteLine(workFilePath, "忽略空文件夹 = " + path, true);
                        break;
                    } //勾选了忽略空文件夹，遇空文件夹时跳出
                    directories = Directory.GetDirectories(path); //将当前文件夹内所有子文件夹（路径名称）以字符形式存入字符组
                    if (directories.Length == 0 && comboBox1.SelectedIndex == 0) //如果不是同一个父目录
                    {
                        if (dirCount == 0)
                        {
                            dirCount = 1;
                        }
                        else
                        {
                            WriteLine(workFilePath, "", true);
                        }
                        WriteLine(workFilePath, " Directory of " + path, true); //首次输出子文件夹的父目录，表示正在对其检索
                        if (checkBox21.Checked || checkBox22.Checked)
                        {
                            if (checkBox21.Checked)
                            {
                                j = Directory.GetFiles(path).Length;
                                a = "               " + j.ToString() + " File(s)";
                            }
                            if (checkBox22.Checked)
                            {
                                k = Directory.GetDirectories(path).Length;
                                b = "             " + k.ToString() + " DIR(s)";
                            }
                            WriteLine(workFilePath, a + b, true);
                        }
                        if (j + k > 0) { WriteLine(workFilePath, "", true); }//目录有内容时换行以隔开内容
                    }
                    foreach (var item in directories) //遍历子文件夹字符元素
                    {
                        if (workStop) { break; }
                        if (checkBox14.Checked && IsDirectoryEmpty(item))
                        {
                            //WriteLine(workFilePath, "子目录为空：" + item , true);
                            continue; //勾选了忽略空文件夹，但部分子文件夹为空亦跳过
                        }

                        if (checkBox19.Checked && !IsDirectoryEmptyUserDef(item))
                        {
                            continue; //勾选了忽略非空文件夹（只输出空文件夹），那么非空文件夹将被跳过
                        }
                        //通配符错误时重置
                        if (textBox5.Text == "*") { }
                        else if (
                            string.Compare(
                                Path.GetExtension(item),
                                textBox5.Text,
                                checkBox26.Checked
                            ) != 0
                        )
                        {
                            continue;
                        }
                        if (checkBox38.Checked && textBox12.Text != "")
                        {
                            if (checkBox36.Checked)
                            {
                                if (!Regex.IsMatch(item.Substring(item.LastIndexOf("\\") + 1), textBox12.Text)) { continue; }
                            }
                            else
                            {
                                //正则或特征不匹配的话，下一个
                                if (checkBox37.Checked)
                                {
                                    //忽略大小写
                                    //IndexOf 函数对大小写不敏感，适用于不区分大小写的判断，返回值为int型（在sring中的索引值）
                                    if (item.Substring(item.LastIndexOf("\\") + 1).IndexOf(textBox12.Text, StringComparison.OrdinalIgnoreCase) == -1)
                                    {
                                        continue;
                                    }
                                }
                                else
                                {
                                    //String.Contains对大小写敏感，适用于区分大小写的判断
                                    if (!item.Substring(item.LastIndexOf("\\") + 1).Contains(textBox12.Text))
                                    {
                                        continue;
                                    }
                                }
                            }
                        }


                        if (checkBox27.Checked && !ParamIsInRange(item))
                        {
                            continue;
                        }
                        switch (comboBox1.SelectedIndex)
                        {
                            case 0:
                                //WriteLine(workFilePath, "目录：" + item.Substring(0, item.LastIndexOf("\\"))+" =>上回："+ tempStr, true);
                                if (item.Substring(0, item.LastIndexOf("\\")) != tempStr) //如果不是同一个父目录
                                {
                                    tempStr = item.Substring(0, item.LastIndexOf("\\"));
                                    if (dirCount == 0)
                                    {
                                        dirCount = 1;
                                    }
                                    else
                                    {
                                        WriteLine(workFilePath, "", true);
                                    }
                                    WriteLine(workFilePath, " Directory of " + tempStr, true); //首次输出子文件夹的父目录，表示正在对其检索
                                    if (checkBox21.Checked || checkBox22.Checked)
                                    {
                                        if (checkBox21.Checked)
                                        {
                                            j = Directory.GetFiles(path).Length;
                                            a = "               " + j.ToString() + " File(s)";
                                        }
                                        if (checkBox22.Checked)
                                        {
                                            k = Directory.GetDirectories(path).Length;
                                            b = "             " + k.ToString() + " DIR(s)";
                                        }
                                        WriteLine(workFilePath, a + b, true);
                                    }
                                    if (j + k > 0) { WriteLine(workFilePath, "", true); }//目录有内容时换行以隔开内容
                                }

                                if (checkBox12.Checked) //勾选文件夹全路径
                                {
                                    dirName = item;
                                }
                                else
                                {
                                    dirName = item.Substring(item.LastIndexOf("\\") + 1);
                                }
                                directoryInfo = new DirectoryInfo(item); //根据子文件夹字符元素建立其文件夹信息实例
                                if (checkBox20.Checked)
                                {
                                    dirSize = CountSize(GetDirectoryLength(item));
                                }
                                if (checkBox17.Checked)
                                {
                                    dirTime = directoryInfo.LastWriteTime.ToString();
                                }
                                dirName = dirTime + " <DIR> " + dirSize + " " + dirName;
                                WriteLine(workFilePath, dirName, true);
                                break;
                            default:
                                //修改文件夹动作
                                if (checkBox25.Checked)
                                {
                                    DAction(workFilePath, item);
                                }
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 检索文件并按用户功能动作（不含子文件夹遍历）
        /// </summary>
        /// <param name="path"></param>
        /// <param name="workFilePath"></param>
        private void FPrint(string path, string workFilePath)
        {
            string[] files;
            string fileName;
            string newFileName;
            string newFilePath;
            string fileSize = "";
            string fileTime = "";
            //string tempStr = "";
            //string a = "";
            //string b = "";
            DirectoryInfo directoryInfo;
            FileInfo[] fileInfos;
            if (checkBox3.Checked) //允许检索文件
            {
                for (int i = 0; i < 1; i++)
                {
                    if (checkBox15.Checked) //输出文件后缀
                    {
                        files = Directory.GetFiles(path);
                        foreach (var item in files) //处理每个遍历到的文件名字符
                        {
                            if (workStop) { break; }
                            //WriteLine(workFilePath, "文件大小" + CountSize(GetFileLength(item)), true);//测试专用
                            if (checkBox24.Checked && CountSize(GetFileLength(item)) == "0 Byte")
                            {
                                continue; //勾选了忽略空文件，遇空文件时跳过
                            }
                            if (checkBox23.Checked && !(CountSize(GetFileLength(item)) == "0 Byte"))
                            {
                                continue; //勾选了忽略非空文件，遇非空文件时跳过
                            }
                            //通配符错误时重置
                            if (textBox5.Text == "*") { }
                            else if (
                                string.Compare(
                                    Path.GetExtension(item),
                                    textBox5.Text,
                                    checkBox26.Checked
                                ) != 0
                            )
                            {
                                continue;
                            }
                            if (checkBox38.Checked && textBox12.Text != "")
                            {
                                if (checkBox36.Checked)
                                {
                                    if (!Regex.IsMatch(item.Substring(item.LastIndexOf("\\") + 1), textBox12.Text)) { continue; }
                                }
                                else
                                {
                                    //正则或特征不匹配的话，下一个
                                    if (checkBox37.Checked)
                                    {
                                        //忽略大小写
                                        //IndexOf 函数对大小写不敏感，适用于不区分大小写的判断，返回值为int型（在sring中的索引值）
                                        if (item.Substring(item.LastIndexOf("\\") + 1).IndexOf(textBox12.Text, StringComparison.OrdinalIgnoreCase) == -1)
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        //String.Contains对大小写敏感，适用于区分大小写的判断
                                        if (!item.Substring(item.LastIndexOf("\\") + 1).Contains(textBox12.Text))
                                        {
                                            continue;
                                        }
                                    }
                                }
                            }
                            if (checkBox27.Checked && !ParamIsInRange(item))
                            {
                                continue;
                            }
                            switch (comboBox1.SelectedIndex)
                            {
                                case 0:
                                    if (checkBox11.Checked) //勾选文件全路径
                                    {
                                        fileName = item;
                                    }
                                    else
                                    {
                                        fileName = item.Substring(item.LastIndexOf("\\") + 1);
                                    }
                                    FileInfo fileInfo = new FileInfo(item);
                                    if (checkBox8.Checked)
                                    {
                                        fileSize = CountSize(GetFileLength(item));
                                    }
                                    if (checkBox18.Checked)
                                    {
                                        fileTime = fileInfo.LastWriteTime.ToString();
                                    }
                                    fileName = fileTime + " " + fileSize + " " + fileName;
                                    WriteLine(workFilePath, fileName, true);
                                    break;
                                default:
                                    if (checkBox29.Checked)
                                    {
                                        DAction(workFilePath, item);
                                    }
                                    break;
                            }
                        }
                    }
                    else //不输出文件后缀
                    {
                        directoryInfo = new DirectoryInfo(path);
                        fileInfos = directoryInfo.GetFiles();
                        foreach (FileInfo fileInfo in fileInfos)
                        {
                            if (workStop) { break; }
                            if (
                                checkBox24.Checked
                                && CountSize(GetFileLength(fileInfo.Name)) == "0 Byte"
                            )
                            {
                                continue; //勾选了忽略空文件，遇空文件时跳过
                            }
                            if (
                                checkBox23.Checked
                                && !(CountSize(GetFileLength(fileInfo.Name)) == "0 Byte")
                            )
                            {
                                continue; //勾选了忽略非空文件，遇非空文件时跳过
                            }
                            //通配符错误时重置
                            if (textBox5.Text == "*") { }
                            else if (
                                string.Compare(
                                    Path.GetExtension(fileInfo.Name),
                                    textBox5.Text,
                                    checkBox26.Checked
                                ) != 0
                            )
                            {
                                continue;
                            }
                            if (checkBox38.Checked && textBox12.Text != "")
                            {
                                if (checkBox36.Checked)
                                {
                                    if (!Regex.IsMatch(fileInfo.Name.Substring(fileInfo.Name.LastIndexOf("\\") + 1), textBox12.Text)) { continue; }
                                }
                                else
                                {
                                    //正则或特征不匹配的话，下一个
                                    if (checkBox37.Checked)
                                    {
                                        //忽略大小写
                                        //IndexOf 函数对大小写不敏感，适用于不区分大小写的判断，返回值为int型（在sring中的索引值）
                                        if (fileInfo.Name.Substring(fileInfo.Name.LastIndexOf("\\") + 1).IndexOf(textBox12.Text, StringComparison.OrdinalIgnoreCase) == -1)
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        //String.Contains对大小写敏感，适用于区分大小写的判断
                                        if (!fileInfo.Name.Substring(fileInfo.Name.LastIndexOf("\\") + 1).Contains(textBox12.Text))
                                        {
                                            continue;
                                        }
                                    }
                                }
                            }
                            if (checkBox27.Checked && !ParamIsInRange(fileInfo.Name))
                            {
                                continue;
                            }
                            switch (comboBox1.SelectedIndex)
                            {
                                case 0:
                                    if (checkBox11.Checked) //勾选允许文件全路径
                                    {
                                        newFilePath = Path.Combine(
                                            fileInfo.DirectoryName,
                                            Path.GetFileNameWithoutExtension(fileInfo.Name)
                                        );
                                        if (checkBox8.Checked)
                                        {
                                            fileSize = CountSize(GetFileLength(fileInfo.FullName));
                                        }
                                        if (checkBox18.Checked)
                                        {
                                            fileTime = fileInfo.LastWriteTime.ToString();
                                        }
                                        newFilePath = fileTime + " " + fileSize + " " + newFilePath;
                                        WriteLine(workFilePath, newFilePath, true);
                                    }
                                    else
                                    {
                                        newFileName = Path.GetFileNameWithoutExtension(
                                            fileInfo.Name
                                        );
                                        if (checkBox8.Checked)
                                        {
                                            fileSize = CountSize(GetFileLength(fileInfo.FullName));
                                        }
                                        if (checkBox18.Checked)
                                        {
                                            fileTime = fileInfo.LastWriteTime.ToString();
                                        }
                                        newFileName = fileTime + " " + fileSize + " " + newFileName;
                                        WriteLine(workFilePath, newFileName, true);
                                    }
                                    break;
                                default:
                                    if (checkBox29.Checked)
                                    {
                                        DAction(workFilePath, fileInfo.FullName);
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 勾选文件夹与文件分列时的文件夹处理动作
        /// </summary>
        /// <param name="workPath"></param>
        /// <param name="workFilePath"></param>
        private void DirRun(string workPath, string workFilePath)
        {
            string dirName;
            string[] directories;
            string dirSize = "";
            string dirTime = "";
            DirectoryInfo directoryInfo;
            if (checkBox4.Checked) //允许检索文件夹
            {
                for (int i = 0; i < 1; i++)
                {
                    if (!checkBox10.Checked)
                    {
                        //不允许遍历子文件夹，仅遍历当前目录文件夹
                        directories = Directory.GetDirectories(workPath);
                    }
                    else
                    {
                        //允许遍历子文件夹下所有文件夹
                        directories = Directory.GetDirectories(
                            workPath,
                            "*",
                            SearchOption.AllDirectories
                        );
                    }
                    foreach (var item in directories) //处理每个遍历到的文件夹
                    {
                        if (workStop) { break; }
                        if (checkBox14.Checked && IsDirectoryEmpty(item))
                        {
                            continue; //勾选了忽略空文件夹，但部分子文件夹为空亦跳过
                        }

                        if (checkBox19.Checked && !IsDirectoryEmptyUserDef(item))
                        {
                            continue; //勾选了忽略非空文件夹（只输出空文件夹），那么非空文件夹将被跳过
                        }
                        //通配符错误时重置
                        if (textBox5.Text == "*") { }
                        else if (
                            string.Compare(
                                Path.GetExtension(item),
                                textBox5.Text,
                                checkBox26.Checked
                            ) != 0
                        )
                        {
                            continue;
                        }
                        if (checkBox38.Checked && textBox12.Text != "")
                        {
                            if (checkBox36.Checked)
                            {
                                if (!Regex.IsMatch(item.Substring(item.LastIndexOf("\\") + 1), textBox12.Text)) { continue; }
                            }
                            else
                            {
                                //正则或特征不匹配的话，下一个
                                if (checkBox37.Checked)
                                {
                                    //忽略大小写
                                    //IndexOf 函数对大小写不敏感，适用于不区分大小写的判断，返回值为int型（在sring中的索引值）
                                    if (item.Substring(item.LastIndexOf("\\") + 1).IndexOf(textBox12.Text, StringComparison.OrdinalIgnoreCase) == -1)
                                    {
                                        continue;
                                    }
                                }
                                else
                                {
                                    //String.Contains对大小写敏感，适用于区分大小写的判断
                                    if (!item.Substring(item.LastIndexOf("\\") + 1).Contains(textBox12.Text))
                                    {
                                        continue;
                                    }
                                }
                            }
                        }
                        if (checkBox27.Checked && !ParamIsInRange(item))
                        {
                            continue;
                        }
                        switch (comboBox1.SelectedIndex)
                        {
                            case 0:
                                if (checkBox12.Checked)
                                {
                                    //勾选文件夹全路径
                                    dirName = item;
                                }
                                else
                                {
                                    dirName = item.Substring(item.LastIndexOf("\\") + 1);
                                }
                                if (checkBox20.Checked)
                                {
                                    dirSize = CountSize(GetDirectoryLength(item));
                                }
                                if (checkBox17.Checked)
                                {
                                    directoryInfo = new DirectoryInfo(item);
                                    dirTime = directoryInfo.LastWriteTime.ToString();
                                }
                                dirName = dirTime + " <DIR> " + dirSize + " " + dirName;
                                WriteLine(workFilePath, dirName, true);
                                break;
                            default:
                                //修改文件夹动作
                                if (checkBox25.Checked)
                                {
                                    DAction(workFilePath, item);
                                }
                                break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 勾选文件夹与文件分列时的文件处理动作
        /// </summary>
        /// <param name="workPath"></param>
        /// <param name="workFilePath"></param>
        private void FileRun(string workPath, string workFilePath)
        {
            string fileName;
            string[] files;
            string fileSize = "";
            string fileTime = "";
            string newFileName;
            string newFilePath;
            DirectoryInfo directoryInfo;
            FileInfo[] fileInfos;
            if (checkBox3.Checked) //允许检索文件
            {
                for (int i = 0; i < 1; i++)
                {
                    if (!workStatus) { break; }
                    if (!checkBox10.Checked)
                    {
                        //不允许遍历子文件夹，仅遍历当前目录文件
                        files = Directory.GetFiles(workPath);
                    }
                    else
                    {
                        //允许遍历子文件夹下所有文件
                        files = Directory.GetFiles(workPath, "*", SearchOption.AllDirectories);
                    }
                    if (checkBox15.Checked) //未勾选输出文件后缀
                    {
                        foreach (var item in files) //处理每个遍历到的文件
                        {
                            if (workStop) { break; }
                            if (checkBox24.Checked && CountSize(GetFileLength(item)) == "0 Byte")
                            {
                                continue; //勾选了忽略空文件，遇空文件时跳过
                            }
                            if (checkBox23.Checked && !(CountSize(GetFileLength(item)) == "0 Byte"))
                            {
                                continue; //勾选了忽略非空文件，遇非空文件时跳过
                            }
                            //通配符错误时重置
                            if (textBox5.Text == "*") { }
                            else if (
                                string.Compare(
                                    Path.GetExtension(item),
                                    textBox5.Text,
                                    checkBox26.Checked
                                ) != 0
                            )
                            {
                                continue;
                            }
                            if (checkBox38.Checked && textBox12.Text != "")
                            {
                                if (checkBox36.Checked)
                                {
                                    if (!Regex.IsMatch(item.Substring(item.LastIndexOf("\\") + 1), textBox12.Text)) { continue; }
                                }
                                else
                                {
                                    //正则或特征不匹配的话，下一个
                                    if (checkBox37.Checked)
                                    {
                                        //忽略大小写
                                        //IndexOf 函数对大小写不敏感，适用于不区分大小写的判断，返回值为int型（在sring中的索引值）
                                        if (item.Substring(item.LastIndexOf("\\") + 1).IndexOf(textBox12.Text, StringComparison.OrdinalIgnoreCase) == -1)
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        //String.Contains对大小写敏感，适用于区分大小写的判断
                                        if (!item.Substring(item.LastIndexOf("\\") + 1).Contains(textBox12.Text))
                                        {
                                            continue;
                                        }
                                    }
                                }
                            }
                            if (checkBox27.Checked && !ParamIsInRange(item))
                            {
                                continue;
                            }
                            switch (comboBox1.SelectedIndex)
                            {
                                case 0:
                                    if (checkBox11.Checked)
                                    {
                                        //勾选允许文件全路径
                                        fileName = item;
                                    }
                                    else
                                    {
                                        fileName = item.Substring(item.LastIndexOf("\\") + 1);
                                    }

                                    if (checkBox8.Checked)
                                    {
                                        fileSize = CountSize(GetFileLength(item));
                                    }
                                    if (checkBox18.Checked)
                                    {
                                        FileInfo fileInfo = new FileInfo(item);
                                        fileTime = fileInfo.LastWriteTime.ToString();
                                    }
                                    fileName = fileTime + " " + fileSize + " " + fileName;
                                    WriteLine(workFilePath, fileName, true);
                                    break;
                                default:
                                    //修改文件夹动作
                                    if (checkBox29.Checked)
                                    {
                                        DAction(workFilePath, item);
                                    }
                                    break;
                            }
                        }
                    }
                    else
                    {
                        directoryInfo = new DirectoryInfo(workPath);
                        fileInfos = directoryInfo.GetFiles();
                        foreach (FileInfo fileInfo in fileInfos)
                        {
                            if (workStop) { break; }
                            if (
                                checkBox24.Checked
                                && CountSize(GetFileLength(fileInfo.Name)) == "0 Byte"
                            )
                            {
                                continue; //勾选了忽略空文件，遇空文件时跳过
                            }
                            if (
                                checkBox23.Checked
                                && !(CountSize(GetFileLength(fileInfo.Name)) == "0 Byte")
                            )
                            {
                                continue; //勾选了忽略非空文件，遇非空文件时跳过
                            }
                            //通配符错误时重置
                            if (textBox5.Text == "*") { }
                            else if (
                                string.Compare(
                                    Path.GetExtension(fileInfo.Name),
                                    textBox5.Text,
                                    checkBox26.Checked
                                ) != 0
                            )
                            {
                                continue;
                            }
                            if (checkBox38.Checked && textBox12.Text != "")
                            {
                                if (checkBox36.Checked)
                                {
                                    if (!Regex.IsMatch(fileInfo.Name.Substring(fileInfo.Name.LastIndexOf("\\") + 1), textBox12.Text)) { continue; }
                                }
                                else
                                {
                                    //正则或特征不匹配的话，下一个
                                    if (checkBox37.Checked)
                                    {
                                        //忽略大小写
                                        //IndexOf 函数对大小写不敏感，适用于不区分大小写的判断，返回值为int型（在sring中的索引值）
                                        if (fileInfo.Name.Substring(fileInfo.Name.LastIndexOf("\\") + 1).IndexOf(textBox12.Text, StringComparison.OrdinalIgnoreCase) == -1)
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        //String.Contains对大小写敏感，适用于区分大小写的判断
                                        if (!fileInfo.Name.Substring(fileInfo.Name.LastIndexOf("\\") + 1).Contains(textBox12.Text))
                                        {
                                            continue;
                                        }
                                    }
                                }
                            }
                            if (checkBox27.Checked && !ParamIsInRange(fileInfo.Name))
                            {
                                continue;
                            }
                            switch (comboBox1.SelectedIndex)
                            {
                                case 0:
                                    if (checkBox11.Checked)
                                    {
                                        //勾选允许文件全路径
                                        newFilePath = Path.Combine(
                                            fileInfo.DirectoryName,
                                            Path.GetFileNameWithoutExtension(fileInfo.Name)
                                        );
                                        if (checkBox8.Checked)
                                        {
                                            fileSize = CountSize(GetFileLength(fileInfo.FullName));
                                        }
                                        if (checkBox18.Checked)
                                        {
                                            fileTime = fileInfo.LastWriteTime.ToString();
                                        }
                                        newFilePath = fileTime + " " + fileSize + " " + newFilePath;
                                        WriteLine(workFilePath, newFilePath, true);
                                    }
                                    else
                                    {
                                        newFileName = Path.GetFileNameWithoutExtension(
                                            fileInfo.Name
                                        );
                                        if (checkBox8.Checked)
                                        {
                                            fileSize = CountSize(GetFileLength(fileInfo.FullName));
                                        }
                                        if (checkBox18.Checked)
                                        {
                                            fileTime = fileInfo.LastWriteTime.ToString();
                                        }
                                        newFileName = fileTime + " " + fileSize + " " + newFileName;
                                        WriteLine(workFilePath, newFileName, true);
                                    }
                                    break;
                                default:
                                    if (checkBox29.Checked)
                                    {
                                        DAction(workFilePath, fileInfo.FullName);
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region Work
        /// <summary>
        /// 窗口初始化
        /// </summary>
        public Form1()
        {
            InitializeComponent();
            label11.ForeColor = Color.Red;
            label9.Text = "检索后缀（如.txt）";
            if (comboBox1.SelectedIndex == -1)
            {
                comboBox1.SelectedIndex = 0;
            }
            if (comboBox3.SelectedIndex == -1)
            {
                comboBox3.SelectedIndex = 0;
            }
            if (comboBox4.SelectedIndex == -1)
            {
                comboBox4.SelectedIndex = 1;
            }
        }

        /// <summary>
        /// 选择工作文件夹
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void button1_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog();
            fbd.ShowDialog();
            string path = fbd.SelectedPath;
            textBox1.Text = path;
        }

        /// <summary>
        /// 选择工作文本
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void button3_Click(object sender, EventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog();
            ofd.ShowDialog();
            string path = ofd.FileName;
            textBox2.Text = path;
        }

        /// <summary>
        /// 点击执行所选功能
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void button2_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < 1; i++)
            {
                if (IsValidDFPath(textBox1.Text))
                {
                    UserOpEnable(false);
                }
                else { break; }
                if (button2.Text == "执行" && workStatus == false)
                {
                    workStatus = true;
                    button2.Text = "取消";
                    th = new Thread(ButtonRun) { IsBackground = true };
                    th.Start();

                }
                else if (button2.Text == "取消" && workStatus == true)
                {
                    workStop = true;
                }
            }

            //中止进程做法，但是有问题↓
            //if (button2.Text == "执行" && workStatus == false)
            //{
            //    th = new Thread(ButtonRun) { IsBackground = true };
            //    th.Start();

            //}
            //else if (button2.Text == "取消" && workStatus == true)
            //{
            //    workStatus = false;
            //    if (th.IsAlive) { th.Abort(); button2.Text = "执行"; UserOpEnable(true); label11.Text = ""; }
            //}
        }

        /// <summary>
        /// 下拉选择功能
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (checkBox13.Checked)
            {
                textBox8.Text = "";
            } //勾选时，设置参数4为空（即保护任意后缀）
            else
            {
                textBox8.Text = "*";
            }
            switch (comboBox1.SelectedIndex)
            {
                case 0:
                    label8.Text = "批量将文件（夹）名称打印到工作文件";
                    panel2.Visible = false;
                    panel3.Visible = false;
                    panel5.Visible = false;
                    panel9.Visible = false;
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 1:
                    label8.Text = "批量将文件（夹）名称去除固定位数字符（填写要移除的起始位和字符数，字符起始位从0起算）";
                    label4.Text = "起始位（数字）";
                    label5.Text = "移除字符数";
                    label14.Text = "保护后缀";
                    panel2.Visible = true;
                    panel3.Visible = true;
                    panel9.Visible = true;
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 2:
                    label8.Text = "批量将文件（夹）名称保留固定位数字符（填写要保留的起始位和字符数，字符起始位从0起算）";
                    label4.Text = "起始位（数字）";
                    label5.Text = "保留字符数";
                    label14.Text = "保护后缀";
                    panel2.Visible = true;
                    panel3.Visible = true;
                    panel9.Visible = true;
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 3:
                    label8.Text = "批量插入字符到文件（夹）名称固定位数字符前（填写固定字符起始位和字符数，字符起始位从0起算）";
                    label4.Text = "起始位（数字）";
                    label5.Text = "固定字符数";
                    label14.Text = "保护后缀";
                    panel2.Visible = true;
                    panel3.Visible = true;
                    panel9.Visible = true;
                    panel5.Visible = true; //参数5面板
                    label16.Text = "插入字符"; //参数5说明
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 4:
                    label8.Text = "批量插入字符到文件（夹）名称固定位数字符后（填写固定字符起始位和字符数，字符起始位从0起算）";
                    label4.Text = "起始位（数字）";
                    label5.Text = "固定字符数";
                    label14.Text = "保护后缀";
                    panel2.Visible = true;
                    panel3.Visible = true;
                    panel9.Visible = true;
                    panel5.Visible = true; //参数5面板
                    label16.Text = "插入字符"; //参数5说明
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 5:
                    label8.Text = "批量插入字符到文件（夹）名称最前（勾选保留后缀则只修改前缀名称）";
                    label4.Text = "起始位（数字）";
                    label5.Text = "固定字符数";
                    label14.Text = "保护后缀";
                    panel2.Visible = false;
                    panel3.Visible = false;
                    panel9.Visible = true;
                    panel5.Visible = true; //参数5面板
                    label16.Text = "插入字符"; //参数5说明
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 6:
                    label8.Text = "批量插入字符到文件（夹）名称最后（注：保护后缀要将参数4留空）";
                    label4.Text = "起始位（数字）";
                    label5.Text = "保留字符数";
                    label14.Text = "保护后缀";
                    panel2.Visible = false;
                    panel3.Visible = false;
                    panel9.Visible = true;
                    panel5.Visible = true; //参数5面板
                    label16.Text = "插入字符"; //参数5说明
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 7:
                    label8.Text = "批量替换文件（夹）名称固定位数字符";
                    label4.Text = "起始位（数字）";
                    label5.Text = "替换字符数";
                    label14.Text = "保护后缀";
                    panel2.Visible = true;
                    panel3.Visible = true;
                    panel9.Visible = true;
                    panel5.Visible = true; //参数5面板
                    label16.Text = "替换后缀"; //参数5说明
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 8:
                    label8.Text = "批量替换文件（夹）名称的指定字符";
                    label14.Text = "保护后缀";
                    panel2.Visible = false;
                    panel3.Visible = false;
                    panel9.Visible = true;
                    panel5.Visible = true; //参数5面板
                    label16.Text = "原字符"; //参数5说明
                    panel6.Visible = true; //参数6面板
                    label18.Text = "替换字符"; //参数6说明
                    panel10.Visible = false; //参数8面板
                    break;
                case 9:
                    label8.Text = "批量替换文件（夹）名称的后缀字符";
                    label14.Text = "保护后缀";
                    panel2.Visible = false;
                    panel3.Visible = false;
                    panel9.Visible = true;//保护后缀
                    panel5.Visible = true; //参数5面板
                    label16.Text = "替换后缀"; //参数5说明
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
                case 10:
                    label8.Text = "批量删除（移动）指定名称的文件（夹），选此功能时，参数5支持正则表达式";
                    label14.Text = "保护该后缀文件不被删";
                    checkBox13.Checked = false;
                    panel2.Visible = false;
                    panel3.Visible = false;
                    panel9.Visible = true;//保护后缀
                    panel5.Visible = false; //参数5面板
                    panel6.Visible = false; //参数6面板
                    label21.Text = "回收目录"; //参数8说明
                    panel10.Visible = true; //参数8面板
                    break;
                default:
                    label8.Text = "功能未选择！";
                    panel2.Visible = false;
                    panel3.Visible = false;
                    panel5.Visible = false;
                    panel9.Visible = false;
                    panel6.Visible = false; //参数6面板
                    panel10.Visible = false; //参数8面板
                    break;
            }
        }

        /// <summary>
        /// 工作文本变化时检查并修正
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void checkBox7_CheckedChanged(object sender, EventArgs e)
        {
            if (textBox2.Text == "")
            {
                textBox2.Text = AppDomain.CurrentDomain.BaseDirectory + "temp.txt";
            }
            else if (
                checkBox7.Checked
                && (
                    !textBox2.Text.Contains(@".txt")
                    || !Regex.IsMatch(textBox2.Text, @"^(.*)(\.txt)$")
                )
            )
            {
                textBox2.Text += @".txt";
            }
        }

        /// <summary>
        /// 功能1_对删除字符起始位置进行检查并提示
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void textBox4_TextChanged(object sender, EventArgs e)
        {
            if (!IsNumeric(textBox4.Text))
            {
                textBox4.Text = "请重填！";
            }
            else
            {
                startIndex = Convert.ToInt32(textBox4.Text);
                if (startIndex < 0)
                {
                    textBox4.Text = "请重填！";
                }
            }
        }

        /// <summary>
        /// 功能1_对删除字符数检查并提示
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void textBox3_TextChanged(object sender, EventArgs e)
        {
            if (!IsNumeric(textBox3.Text))
            {
                textBox3.Text = "请重填！";
            }
            else
            {
                strCount = Convert.ToInt32(textBox3.Text);
                if (strCount < 0)
                {
                    textBox3.Text = "请重填！";
                }
            }
        }

        /// <summary>
        /// 对检索后缀功能检查并重置
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void textBox5_TextChanged(object sender, EventArgs e)
        {
            if (textBox5.Text == "")
            {
                textBox5.Text = "*";
            }
        }

        /// <summary>
        /// 用户快捷选择工作文档
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void comboBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            string temp;
            switch (comboBox2.SelectedIndex)
            {
                case 0: //文本>用户自定
                    break;
                case 1: //文本>程序目录
                    textBox2.Text = AppDomain.CurrentDomain.BaseDirectory + @"temp.txt";
                    break;
                case 2: //文本>工作目录（外）
                    textBox2.Text = textBox1.Text;
                    if (textBox2.Text == "")
                    {
                        textBox2.Text = AppDomain.CurrentDomain.BaseDirectory + "temp.txt";
                    }
                    else if (
                        checkBox7.Checked
                        && (
                            !textBox2.Text.Contains(@".txt")
                            || !Regex.IsMatch(textBox2.Text, @"^(.*)(\.txt)$")
                        )
                    )
                    {
                        textBox2.Text += @".txt";
                    }
                    break;
                case 3: //文本>工作目录（内）
                    textBox2.Text = textBox1.Text;
                    if (textBox2.Text == "")
                    {
                        textBox2.Text = AppDomain.CurrentDomain.BaseDirectory + "temp.txt";
                    }
                    else if (
                        checkBox7.Checked
                        && (
                            !textBox2.Text.Contains(@".txt")
                            || !Regex.IsMatch(textBox2.Text, @"^(.*)(\.txt)$")
                        )
                    )
                    {
                        temp = textBox2.Text.Substring(textBox2.Text.LastIndexOf("\\") + 1);
                        textBox2.Text += @"\" + temp + @".txt";
                    }
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// 可点击的标签框提示
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void label11_Click(object sender, EventArgs e)
        {
            if (workStatus)
            {
                label11.Text = proCount.ToString() + @"/" + proCountMax.ToString();
                if (label11.Text == @"0/0")
                {
                    label11.Text = "计算中...";
                }
            }
        }

        /// <summary>
        /// 取消跨线程的访问，自动运行
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Form1_Load(object sender, EventArgs e)
        {
            CheckForIllegalCrossThreadCalls = false;
        }

        /// <summary>
        /// 文件（夹）检索大小（Min）
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void textBox6_TextChanged(object sender, EventArgs e)
        {
            if (!IsNumeric(textBox6.Text))
            {
                textBox6.Text = "文件（夹）检索大小（Min）";
            }
        }

        /// <summary>
        /// 文件（夹）检索大小（Max）
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void textBox7_TextChanged(object sender, EventArgs e)
        {
            if (!IsNumeric(textBox7.Text))
            {
                textBox7.Text = "文件（夹）检索大小（Max）";
            }
        }

        private void checkBox13_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox13.Checked)
            {
                textBox8.Text = "";
            } //勾选时，设置参数4为空（即保护任意后缀）
            else
            {
                textBox8.Text = "*";
            }
        }

        private void checkBox20_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox20.Checked)
            {
                label11.Text = "你选择了文件夹递归统计大小，目前非常耗时哦！";
            }
            else
            {
                label11.Text = "";
            }
        }

        private void checkBox25_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox25.Checked)
            {
                checkBox10.Checked = false;
                label11.Text = "目前文件夹修改时不支持遍历子文件夹哦！";
            }
        }

        private void checkBox10_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox10.Checked)
            {
                if (checkBox25.Checked)
                {
                    checkBox25.Checked = false;
                    label11.Text = "目前遍历子文件夹时不支持文件夹修改哦！";
                }
                else
                {

                    if (comboBox1.SelectedIndex == 0) { checkBox34.Visible = true; label11.Text = "遍历子文件夹很耗时！若只想打印文件（夹）名称，可勾选BAT方式！"; }
                    else
                    {
                        checkBox34.Checked = false;
                        checkBox34.Visible = false; label11.Text = "遍历子文件夹会增加耗时！";
                    }
                }
            }
            else
            {
                label11.Text = "";
                checkBox34.Checked = false;
                checkBox34.Visible = false;
            }
        }

        private void textBox9_TextChanged(object sender, EventArgs e) { }

        private void textBox10_TextChanged(object sender, EventArgs e) { }

        private void textBox8_TextChanged(object sender, EventArgs e)
        {
            if (textBox8.Text == "*")
            {
                checkBox13.Checked = false;
            }
            else if (textBox8.Text == "")
            {
                checkBox13.Checked = true;
            }
        }

        private void button4_Click(object sender, EventArgs e)
        {
            FolderBrowserDialog fbd = new FolderBrowserDialog();
            fbd.ShowDialog();
            string path = fbd.SelectedPath;
            textBox11.Text = path;
        }

        private void comboBox4_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (comboBox4.SelectedIndex)
            {
                case 0:
                    textBox11.Enabled = true;
                    button4.Enabled = true;
                    break;
                case 1:
                    textBox11.Enabled = false;
                    button4.Enabled = false;
                    break;
                case 2:
                    textBox11.Enabled = false;
                    button4.Enabled = false;
                    break;
                default:
                    break;

            }
        }
        private void checkBox36_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox36.Checked) { checkBox37.Visible = false; } else { checkBox37.Visible = true; }
        }

        private void checkBox38_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox38.Checked) { panel11.Visible = true; } else { panel11.Visible = false; }
        }
        #endregion
    }
}
