﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KKday.Web.OCBT.AppCode;
using KKday.Web.OCBT.Models.Model.DataModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using KKday.Web.OCBT.Models.Repository;
using KKday.Web.OCBT.Service;
using KKday.Web.OCBT.Models.Model.Order;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace KKday.Web.OCBT.V1
{
    [Route("api/v1/[controller]")]
    public class ComboBookingController : Controller
    {
        private IRedisHelper _redisHelper;
        private readonly ComboBookingRepository _comboRepos;
        private readonly AmazonS3Service _amazonS3Service;
        public ComboBookingController(IRedisHelper redisHelper, ComboBookingRepository comboRepos, AmazonS3Service amazonS3Service)
        {
            _redisHelper = redisHelper;
            _comboRepos = comboRepos;
            _amazonS3Service = amazonS3Service;
        }
        // GET: api/values
        [HttpPost("CrtOrder")]
        public ResponseJson CrtOrder([FromBody] BookingRequestModel request)
        {
            _redisHelper.Push("ComboBookingKeys", JsonConvert.SerializeObject(request));//將Java資料傳入redisQueue
            return new ResponseJson
            {
                metadata=new ResponseMetaModel {
                    status= "1000",
                    description="正確無誤"
                }
            };//立即return 回java
        }

        [HttpGet("ThrowQueue")]
        public string throwQueue(string order_mid)
        {
            var pushData = new 
            {
                master_order_mid=order_mid
            };
            _redisHelper.Push("ComboBookingVoucher", JsonConvert.SerializeObject(pushData));//將Java資料傳入redisQueue
            return "OK";
        }

        /// <summary>
        /// Get From S3, Byte[] Convert To Base64
        /// </summary>
        /// <returns></returns>
        [HttpPost("ConvertBase64")]
        public ConvertBase64Rs ConvertBase64([FromBody] ConvertBase64Rq rq)
        {
            ConvertBase64Rs rs = new ConvertBase64Rs();
            rs.metadata = new ResponseMetaModel
            {
                status = "3002",
                description = "回傳檔案失敗:"
            };

            try
            {
                if (string.IsNullOrEmpty(rq?.fileUrl))
                {
                    rs.metadata.description += "fileUrl can not null";
                }
                else
                {
                    // Rq Log
                    Website.Instance.logger.Info($"ComboBooking Start Get S3: FileName = {rq.fileUrl}");
                    // 將子單狀態 Update CB 並取出 母+子 單 Xid
                    var xid = _comboRepos.UpdateBookingDtlCB(rq.fileUrl);
                    if (!string.IsNullOrEmpty(xid.mst) && !string.IsNullOrEmpty(xid.dtl))
                    {
                        // Get From S3
                        var getByte = _amazonS3Service.GetObject(rq.fileUrl).Result;
                        // Rs Log
                        Website.Instance.logger.Info($"Get S3 Rs = {JsonConvert.SerializeObject(getByte)}");

                        if (getByte != null)
                        {
                            if (getByte.Success)
                            {
                                rs.metadata.status = "3001";
                                rs.metadata.description = "回傳檔案成功";
                                // Byte[] Convert to Base64
                                rs.data = new ResponseDataModel
                                {
                                    base64str = Convert.ToBase64String(getByte.DataBytes)
                                };
                                // Update Dtl.Status GL
                                var updGL = _comboRepos.UpdateDtlVoucherStatus(Convert.ToInt64(xid.dtl), "GL");
                                // Check All Dtl GL then Update Mst.Status GL
                                var dtlList = _comboRepos.QueryBookingDtl(Convert.ToInt64(xid.mst));
                                if (dtlList?.Count > 0)
                                {
                                    var glList = dtlList.Where(s => s.booking_dtl_voucher_status == "GL")?.Count() ?? 0;
                                    if (dtlList.Count == glList)
                                    {
                                        // Update Mst.Status GL 
                                        var updMstGL = _comboRepos.UpdateMstVoucherStatus(Convert.ToInt64(xid.mst), "GL");
                                    }
                                }
                            }
                            else
                            {
                                // Get S3 Fail
                                rs.metadata.description = "不存在此檔案";
                                // Download Fail
                                _comboRepos.UpdateDtlVoucherStatus(Convert.ToInt64(xid.dtl), "DOWNLOAD_FAIL");
                                _comboRepos.UpdateMstVoucherStatus(Convert.ToInt64(xid.mst), "DOWNLOAD_FAIL");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Website.Instance.logger.Fatal($"ComboBooking_ConvertBase64_Exception:GuidKey ={rq?.requestUuid}, Message={ex.Message}, StackTrace={ex.StackTrace}");
                rs.metadata.description += $" Msg = {ex.Message} , StackTrace = {ex.StackTrace}";
            }

            return rs;
        }

        /// <summary>
        /// 檢查來自 B2D Webhook 通知的單是否為OCBT子單
        /// </summary>
        /// <param name="rq"></param>
        [HttpPost("CheckOrderFromB2D")]
        public void CheckOrderFromB2D([FromBody] CheckOrderFromB2dRqModel rq)
        {
            try
            {
                if (!string.IsNullOrEmpty(rq?.result_type))
                {
                    if (rq?.result_type == "order" && !string.IsNullOrEmpty(rq.order?.order_no))
                    {
                        // 檢查是否為OCBT子單，同時update 為處理中
                        _comboRepos.CheckOrderFromB2d(rq.order.order_no);
                    }
                }
            }
            catch (Exception ex)
            {
                Website.Instance.logger.Fatal($"ComboBooking_CheckOrderFromB2D_Exception: Message={ex.Message}, StackTrace={ex.StackTrace}");
            }
        }
    }
}