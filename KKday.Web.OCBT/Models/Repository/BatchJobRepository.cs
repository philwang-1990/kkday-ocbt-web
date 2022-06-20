﻿using System;
using Dapper;
using KKday.Web.OCBT.AppCode;
using KKday.Web.OCBT.Models.Model.DataModel;
using Npgsql;
using System.Linq;
using System.Collections.Generic;
using KKday.Web.OCBT.Models.Model.Order;
using KKday.Web.OCBT.Models.Repository;
using KKday.Web.OCBT.Proxy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KKday.Web.OCBT.Models.Repository
{
    public class BatchJobRepository
    {

        private readonly ComboBookingRepository _comboBookingRepos;
        private readonly OrderProxy _orderProxy;
        private readonly SlackHelper _slack;
        public BatchJobRepository(ComboBookingRepository comboBookingRepos, OrderProxy orderProxy,SlackHelper slack)
        {
            _comboBookingRepos = comboBookingRepos;
            _orderProxy = orderProxy;
            _slack = slack;
        }


        public void SetParentBack(string guidKey)
        {
            //1.查詢所有的母單  (1).只要過出發日 (2)不過有沒有正常完成  ，都要由OCBT檢查
            try
            {
                List<ParentOrderModel> orderLst = new  List<ParentOrderModel>();
                SqlMapper.AddTypeHandler(typeof(List<ChildOrderModel>), new ObjectJsonMapper());

                //取comboSupList
                using (var conn = new NpgsqlConnection(Website.Instance.OCBT_DB))
                {
                    //SQL含open date
                    string sqlStmt = @"select a.booking_mst_xid,a.order_mid,a.go_date,a.booking_mst_order_status,a.booking_mst_voucher_status,a.order_oid,a.is_back,a.is_need_back,
jsonb_agg(json_build_object('booking_dtl_xid',b.booking_dtl_xid,'order_mid',b.order_mid,'order_oid',b.order_oid,'booking_dtl_order_status',b.booking_dtl_order_status,'booking_dtl_voucher_status',b.booking_dtl_voucher_status )) as child_order,
'N' as is_open_date
from booking_mst a
left join booking_dtl b on a.booking_mst_xid =b.booking_mst_xid
where a.is_back=false and a.is_need_back=true
and to_date(a.go_date,'yyyyMMdd') <now() and COALESCE(a.go_date,'')!=''
group by a.booking_mst_xid,a.order_mid,a.go_date,a.booking_mst_order_status,a.booking_mst_voucher_status,a.order_oid,a.is_back,a.is_need_back
union all 
select a.booking_mst_xid,a.order_mid,a.go_date,a.booking_mst_order_status,a.booking_mst_voucher_status,a.order_oid,a.is_back,a.is_need_back,
jsonb_agg(json_build_object('booking_dtl_xid',b.booking_dtl_xid,'order_mid',b.order_mid,'order_oid',b.order_oid,'booking_dtl_order_status',b.booking_dtl_order_status,'booking_dtl_voucher_status',b.booking_dtl_voucher_status )) as child_order,
'Y' as is_open_date
from booking_mst a
left join booking_dtl b on a.booking_mst_xid =b.booking_mst_xid
where a.is_back=false and a.is_need_back=true
and COALESCE(a.go_date,'')=''
group by a.booking_mst_xid,a.order_mid,a.go_date,a.booking_mst_order_status,a.booking_mst_voucher_status,a.order_oid,a.is_back,a.is_need_back";

                    orderLst = conn.Query<ParentOrderModel>(sqlStmt).ToList();
                }

                foreach (ParentOrderModel parent in orderLst)
                {
                    //呼叫java母子狀態
                    OrderApiRqModel orderApi = new OrderApiRqModel()
                    {
                        apiKey = Website.Instance.Configuration["KKdayAPI:Body:ApiKey"],
                        userOid = Website.Instance.Configuration["KKdayAPI:Body:UserOid"],
                        locale = "zh-tw",
                        ipaddress = _comboBookingRepos.GetLocalIPAddress()
                    };

                    Dictionary<string, object> json = new Dictionary<string, object>();
                    List<string> orderMid = new List<string>(); orderMid.Add(parent.order_mid);
                    json.Add("orderMidList", orderMid);

                    string url = $"{Website.Instance.Configuration["ApiUrl:JAVA"]}v2/order/info/relastionMapping/" + parent.order_mid;
                    string result= _orderProxy.Post(url, JsonConvert.SerializeObject(orderApi,
                                Newtonsoft.Json.Formatting.None,
                                new JsonSerializerSettings
                                {
                                    NullValueHandling = NullValueHandling.Ignore
                                }), guidKey);

                    relastionMappingContentResModel mapping = JsonConvert.DeserializeObject<relastionMappingContentResModel>(result);

                    //如果母單CX & BACK , 直接壓 is_need_back =false // 表示不再確認，並留註記
                    //如果母單GO 要判斷是否子單相同，都以子單的為主，
                    //(1)子單只要是 CX + 其他BACK 就回壓BACK , is_back=true 並留註記
                    //(2)全CX 就直接壓 is_need_back =false // 表示不再確認，並留註記
                    if (mapping.parentOrderStatus == "BACK" || mapping.parentOrderStatus == "CX")
                    {
                        this.UdpIsNeedBack(guidKey, parent.booking_mst_xid, false);
                    }
                    else if (mapping.parentOrderStatus == "GO")
                    {
                        if (mapping.orderList.Select(x => x.orderStatus == "GO").Count() > 1)
                        {
                        }
                        else if (mapping.orderList.Select(x => x.orderStatus == "CX").Count() == mapping.orderList.Count)
                        {
                            //子單全CX
                            this.UdpIsNeedBack(guidKey, parent.booking_mst_xid, false);
                        }
                        else if (mapping.orderList.Select(x => x.orderStatus == "BACK").Count() == mapping.orderList.Count)
                        {
                            //CALL JAVA SET BACK
                            url = $"{Website.Instance.Configuration["ApiUrl:JAVA"]}v3/order/statusback/" + parent.order_mid;
                            result = _orderProxy.Post(url, JsonConvert.SerializeObject(orderApi,
                                        Newtonsoft.Json.Formatting.None,
                                        new JsonSerializerSettings
                                        {
                                            NullValueHandling = NullValueHandling.Ignore
                                        }), guidKey);

                            Website.Instance.logger.Fatal($"BatchJobRepository_SetParentBack_result:order_mid:{parent.order_mid}:{result}");

                            var rs = JObject.Parse(result);
                            if (rs["content"]["result"]?.ToString() != "0000")
                            {
                                //警示
                                _slack.SlackPost(guidKey, "SetParentBack", "BatchJobRepository/SetParentBack", $"order_mid:{parent.order_mid},SetParentBack回覆失敗", $"Result ={ result}");
                            }

                            this.UdpIsBack(guidKey, parent.booking_mst_xid, true);
                        }
                        else if (mapping.orderList.Select(x => x.orderStatus == "BACK").Count() > 1)
                        {
                            //CX +BACK ==TOTAL
                            var cxCount = mapping.orderList.Select(x => x.orderStatus == "CX").Count();
                            var backCount = mapping.orderList.Select(x => x.orderStatus == "BACK").Count();
                            if ((cxCount + backCount) == mapping.orderList.Count)
                            {
                                //CALL JAVA SET BACK
                                url = $"{Website.Instance.Configuration["ApiUrl:JAVA"]}v3/order/statusback/" + parent.order_mid;
                                result = _orderProxy.Post(url, JsonConvert.SerializeObject(orderApi,
                                            Newtonsoft.Json.Formatting.None,
                                            new JsonSerializerSettings
                                            {
                                                NullValueHandling = NullValueHandling.Ignore
                                            }), guidKey);
                                Website.Instance.logger.Fatal($"BatchJobRepository_SetParentBack_result:order_mid:{parent.order_mid}:{result}");

                                var rs = JObject.Parse(result);
                                if (rs["content"]["result"]?.ToString() != "0000")
                                {
                                    //警示
                                    _slack.SlackPost(guidKey, "SetParentBack", "BatchJobRepository/SetParentBack", $"order_mid:{parent.order_mid},SetParentBack回覆失敗", $"Result ={ result}");
                                }
                                this.UdpIsBack(guidKey, parent.booking_mst_xid, true);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Website.Instance.logger.Fatal($"BatchJobRepository_SetParentBack_exception:GuidKey={guidKey}, Message={ex.Message}, StackTrace={ex.StackTrace}");

            }

        }


        private Boolean UdpIsNeedBack(string guidKey,Int64 bookingMstXid,Boolean isNeedBack)
        {
            try
            {
                using (var conn = new NpgsqlConnection(Website.Instance.OCBT_DB))
                {
                    string sqlStmt = @"update booking_mst set is_need_back=:is_need_back,modify_user='SYSTEM',modify_datetime=now() where booking_mst_xid =:booking_mst_xid";
                    conn.Execute(sqlStmt,new { is_need_back= isNeedBack, bookingMstXid });
                }

                Website.Instance.logger.Info($"BatchJobRepository_UdpIsNeedBack:GuidKey={guidKey},booking_mst_xid:{bookingMstXid} 已為BACK or CX");
            }
            catch (Exception ex)
            {
                Website.Instance.logger.Fatal($"BatchJobRepository_UdpIsNeedBack_exception:GuidKey={guidKey}, Message={ex.Message}, StackTrace={ex.StackTrace}");

            }
            return true;
        }

        private Boolean UdpIsBack(string guidKey, Int64 bookingMstXid, Boolean isBack)
        {
            try
            {
                using (var conn = new NpgsqlConnection(Website.Instance.OCBT_DB))
                {
                    string sqlStmt = @"update booking_mst set is_need_back=:is_back,modify_user='SYSTEM',modify_datetime=now() where booking_mst_xid =:booking_mst_xid";
                    conn.Execute(sqlStmt, new { is_need_back = isBack, bookingMstXid });
                }
                Website.Instance.logger.Info($"BatchJobRepository_UdpIsNeedBack:GuidKey={guidKey},booking_mst_xid:{bookingMstXid} 壓為BACKs");
            }
            catch (Exception ex)
            {
                Website.Instance.logger.Fatal($"BatchJobRepository_UdpIsBack_exception:GuidKey={guidKey}, Message={ex.Message}, StackTrace={ex.StackTrace}");
            }
            return true;
        }
    }
}
