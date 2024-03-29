﻿using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using log4net.Appender;

namespace KKday.Web.OCBT.AppCode
{
    public class MyRollingFileAppender : RollingFileAppender
    {
        public MyRollingFileAppender() : base()
        {
            PreserveLogFileNameExtension = true; // 保留延伸附屬檔名
            StaticLogFileName = true; // 現行檔案不帶任何 Pattern
        }


        protected override void OpenFile(string fileName, bool append)
        {
            base.OpenFile(fileName, append);

            string LogFolder = Path.GetDirectoryName(File);

            SlackHelper helper = new SlackHelper();
            SlackMessageQuery msg = new SlackMessageQuery();
            msg.text = "ocbt web log active file fileName:" + fileName;
            helper.Post(msg);

            var pattern = @"\d{4}-\d{2}-\d{2}";
            var files = Directory.GetFiles(LogFolder, "*.*").Where(f =>
                                f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string file in files)
            {
                // 凡是帶有日期的檔案, 一律視為前一日之前的檔案, 強制刪除之!
                if (Regex.Match(file, pattern, RegexOptions.IgnoreCase).Success)
                {
                    msg.text = "ocbt web del log file:" + file;
                    helper.Post(msg);

                    DeleteFile(file);
                }
            }
        }


        protected override void AdjustFileBeforeAppend()
        {
            base.AdjustFileBeforeAppend();
        }
    }
}
