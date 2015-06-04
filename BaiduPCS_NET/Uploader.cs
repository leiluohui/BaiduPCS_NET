﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BaiduPCS_NET
{
    /// <summary>
    /// 上传器
    /// </summary>
    public class Uploader
    {
        #region 常量

        /// <summary>
        /// 最小分片大小
        /// </summary>
        public const int MIN_UPLOAD_SLICE_SIZE = (512 * 1024);

        /// <summary>
        /// 最大分片大小
        /// </summary>
        public const int MAX_UPLOAD_SLICE_SIZE = (10 * 1024 * 1024);

        /// <summary>
        /// 最大分片数量
        /// </summary>
        public const int MAX_UPLOAD_SLICE_COUNT = 1024;

        #endregion

        #region 属性和事件

        /// <summary>
        /// 获取或设置 Uploader 使用的 BaiduPCS 对象
        /// </summary>
        public BaiduPCS pcs { get; set; }

        /// <summary>
        /// 获取或设置分片数据文件的保存路径。
        /// 上传过程从中断恢复时，将从此目录回复上传状态。
        /// </summary>
        public string slice_dir { get; set; }

        /// <summary>
        /// 当上传进度发送改变时触发
        /// </summary>
        public event EventHandler<ProgressChangeArgs> ProgressChange;

        /// <summary>
        /// 上传分片出错时触发。
        /// </summary>
        public event EventHandler<ErrorArgs> Error;

        /// <summary>
        /// 是否允许快速上传
        /// </summary>
        public bool RapidUploadEnabled { get; set; }

        /// <summary>
        /// 是否允许分片上传。
        /// 分片上传支持断点续传。
        /// </summary>
        public bool SliceUploadEnabled { get; set; }

        /// <summary>
        /// 是否启用进度条
        /// </summary>
        public bool ProgressEnabled { get; set; }

        #endregion

        #region 构造和析构函数

        public Uploader()
            : this(null, string.Empty)
        {
        }

        public Uploader(BaiduPCS pcs, string slice_dir)
        {
            this.pcs = pcs;
            this.slice_dir = slice_dir;

            RapidUploadEnabled = true;
            SliceUploadEnabled = true;
        }

        #endregion

        /// <summary>
        /// 上传一个文件。
        /// 文件大小 小于等于 MIN_UPLOAD_SLICE_SIZE，则直接上传；
        /// 否则，执行快速上传，快速上传失败后执行分片上传。
        /// 分片上传支持断点续传。
        /// </summary>
        /// <param name="localPath">文件的本地绝对路径</param>
        /// <param name="remotePath">文件的网盘绝对路径</param>
        /// <param name="overwrite">如果网盘文件已经存在，是否覆盖原文件。true - 覆盖；false - 自动重命名</param>
        /// <returns>返回上传成功后的网盘中文件的元数据</returns>
        public PcsFileInfo UploadFile(string localPath, string remotePath, bool overwrite = false)
        {
            PcsFileInfo fi = new PcsFileInfo();
            long filesize = new FileInfo(localPath).Length;
            string filemd5 = string.Empty,
                slicemd5;

            //允许快速上传，并且文件大小已经达到快速上传的要求
            if (RapidUploadEnabled && filesize > MIN_UPLOAD_SLICE_SIZE)
            {
                fi = pcs.rapid_upload(remotePath, localPath, out filemd5, out slicemd5, overwrite); //快速上传
                if (!fi.IsEmpty) //上传成功，则直接返回
                    return fi;
            }

            //允许分片上传，并且文件大小已经达到分片上传的要求
            if (SliceUploadEnabled && filesize > MIN_UPLOAD_SLICE_SIZE)
            {
                #region 分片上传，可断点续传

                // 分片文件的存储路径需要文件的 MD5 值来产生，目的是防止上传中断期间，文件被修改。
                if (string.IsNullOrEmpty(filemd5)) // 计算文件的 MD5 值
                {
                    if (!pcs.md5_file(localPath, out filemd5))
                        return new PcsFileInfo(); //未能计算文件的 MD5 值，返回空对象
                }

                SliceOwner owner = new SliceOwner()
                {
                    finished = 0,
                    size = filesize,
                    cancelled = false,
                    filename = localPath
                };

                List<Slice> slicelist;

                //分片文件的存储路径
                string slice_filename = MD5.Encrypt(localPath.ToLower()) + "-" + filemd5 + ".slice";
                if (!string.IsNullOrEmpty(slice_dir))
                    slice_filename = Path.Combine(slice_dir, slice_filename);

                if (File.Exists(slice_filename))
                {
                    // 分片文件存在，则从该文件中还原分片信息
                    slicelist = RestoreSliceList(slice_filename, owner);
                }
                else
                {
                    // 新建分片
                    slicelist = CreateSliceList(owner);
                    //保存一次分片数据
                    SaveSliceList(slice_filename, slicelist);
                }

                // 循环上传每一个分片
                for (int i = 0; i < slicelist.Count; i++)
                {
                    Slice slice = slicelist[i];
                    // 该分片已经上传成功，且得到了其 MD5 值
                    if (slice.status == SliceStatus.Successed)
                        continue;
                    else if (slice.status != SliceStatus.Pending) //上传失败，或正在上传，或正在重试，或用户取消上传
                        slice.status = SliceStatus.Retrying;
                    //当前分片上传失败，且原因不是因为用户取消上传，则重试上传分片。
                    while (!UploadSlice(slice) && slice.status != SliceStatus.Cancelled && !owner.cancelled) ;
                    if (slice.status == SliceStatus.Cancelled)
                        owner.cancelled = true;
                    if (owner.cancelled) //用户取消上传，则终止上传。
                        break;
                    //上传成功，保存分片数据
                    if (slice.status == SliceStatus.Successed)
                        SaveSliceList(slice_filename, slicelist);
                }

                bool suc = !owner.cancelled;
                List<string> md5list = new List<string>();

                #region 检查是否所有分片都上传成功，并创建合并分片的 md5 列表

                if (suc)
                {
                    //检查是否所有分片都上传成功
                    foreach (Slice slice in slicelist)
                    {
                        if (slice.status != SliceStatus.Successed)
                        {
                            suc = false;
                            break;
                        }
                        md5list.Add(slice.md5);
                    }
                }

                #endregion

                if (suc)
                {
                    fi = pcs.create_superfile(remotePath, md5list.ToArray(), overwrite); //合并分片
                    File.Delete(slice_filename); // 删除分片文件
                }
                else
                    fi = new PcsFileInfo();

                return fi;

                #endregion
            }

            #region 直接上传

            if(pcs.ProgressEnabled)
            {
                pcs.Progress += new OnHttpProgressFunction(onProgress);
                pcs.ProgressEnabled = true;
            }
            fi = pcs.upload(remotePath, localPath, overwrite);
            if(pcs.ProgressEnabled)
            {
                pcs.ProgressEnabled = false;
                pcs.Progress -= new OnHttpProgressFunction(onProgress);
            }
            return fi;

            #endregion

        }

        protected bool UploadSlice(Slice slice)
        {
            SliceOwner owner = slice.owner;
            PcsFileInfo fi;
            try
            {
                fi = pcs.upload_slicefile(new OnReadSliceFunction(OnReadSlice), slice, (uint)slice.size);
            }
            catch (Exception ex)
            {
                slice.status = SliceStatus.Failed;
                owner.finished -= slice.finished;
                slice.finished = 0;
                if (Error != null)
                {
                    ErrorArgs arg = new ErrorArgs(null, null, slice, ex);
                    Error(this, arg);
                    if(arg.cancelled)
                    {
                        slice.status = SliceStatus.Cancelled;
                        slice.owner.cancelled = true;
                    }
                }
                return false;
            }
            if (string.IsNullOrEmpty(fi.md5))
            {
                //上传失败，重置其已经上传的数量和整个文件的已经上传数量
                if (slice.status != SliceStatus.Cancelled)
                    slice.status = SliceStatus.Failed;
                owner.finished -= slice.finished;
                slice.finished = 0;
                return false;
            }
            slice.md5 = fi.md5;
            slice.status = SliceStatus.Successed;
            return true;
        }

        /// <summary>
        /// 创建分片
        /// </summary>
        /// <param name="owner">拥有这些分片的 SliceOwner 对象</param>
        /// <returns>返回分片列表</returns>
        protected List<Slice> CreateSliceList(SliceOwner owner)
        {
            List<Slice> slicelist = new List<Slice>();
            
            #region 开始分片

            long filesize = owner.size;
            long slice_count = 0; //分片数量

            // 先按照最小分片计算分片数量
            long slice_size = MIN_UPLOAD_SLICE_SIZE;
            slice_count = (int)(filesize / slice_size);
            if ((filesize % slice_size) != 0)
                slice_count++;

            //分片数量超过最大允许分片数量，因此使用允许的最大分片数量来重新计算每分片的大小
            if (slice_count > MAX_UPLOAD_SLICE_COUNT)
            {
                slice_count = MAX_UPLOAD_SLICE_COUNT;
                slice_size = filesize / slice_count;
                if ((filesize % slice_count) != 0)
                    slice_size++;
                slice_count = (int)(filesize / slice_size);
                if ((filesize % slice_size) != 0) slice_count++;
            }

            long offset = 0;
            for (int i = 0; i < slice_count; i++)
            {
                Slice ts = new Slice()
                {
                    index = i,
                    offset = offset,
                    size = slice_size,
                    finished = 0,
                    status = SliceStatus.Pending,
                    owner = owner
                };
                if (ts.offset + ts.size > filesize) ts.size = filesize - ts.offset;
                offset += slice_size;
                slicelist.Add(ts);
            }

            #endregion

            return slicelist;
        }

        /// <summary>
        /// 从分片文件中还原分片信息
        /// </summary>
        /// <param name="slice_filename">上次上传时，存储的分片文件</param>
        /// <param name="owner">拥有这些分片的 SliceOwner 对象</param>
        /// <returns>返回分片列表</returns>
        protected List<Slice> RestoreSliceList(string slice_filename, SliceOwner owner)
        {
            List<Slice> list = new List<Slice>();
            Slice slice;
            using (FileStream fs = new FileStream(slice_filename, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (BinaryReader br = new BinaryReader(fs))
                {
                    while (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        slice = ReadSlice(br);
                        list.Add(slice);
                        slice.owner = owner;
                        if (slice.status == SliceStatus.Successed)
                            owner.finished += slice.finished;
                        else
                            slice.status = SliceStatus.Pending; // 重置状态为 Pending
                    }
                }
            }
            return list;
        }

        protected void SaveSliceList(string filename, List<Slice> list)
        {
            using (FileStream fs = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.Write))
            {
                using (BinaryWriter br = new BinaryWriter(fs))
                {
                    foreach (Slice slice in list)
                    {
                        WriteSlice(br, slice);
                    }
                }
            }
        }

        protected Slice ReadSlice(BinaryReader br)
        {
            byte[] bs;
            Slice slice = new Slice();
            slice.index = br.ReadInt32();
            slice.offset = br.ReadInt64();
            slice.size = br.ReadInt64();
            slice.finished = br.ReadInt64();
            slice.status = (SliceStatus)br.ReadInt32();
            bs = br.ReadBytes(32);
            slice.md5 = Encoding.ASCII.GetString(bs).Trim('\0').Trim();
            return slice;
        }

        protected void WriteSlice(BinaryWriter br, Slice slice)
        {
            byte[] bs = new byte[32];
            br.Write(slice.index);
            br.Write(slice.offset);
            br.Write(slice.size);
            br.Write(slice.finished);
            br.Write((int)slice.status);
            if (!string.IsNullOrEmpty(slice.md5))
            {
                for (int i = 0; i < 32; i++)
                {
                    bs[i] = (byte)slice.md5[i];
                }
            }
            else
            {
                for (int i = 0; i < 32; i++)
                {
                    bs[i] = 0;
                }
            }
            br.Write(bs);
        }

        protected int OnReadSlice(BaiduPCS sender, out byte[] buf, uint size, uint nmemb, object userdata)
        {
            Slice slice = (Slice)userdata;
            try
            {
                FileStream fs = new FileStream(slice.owner.filename, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                int sz = (int)(size * nmemb);
                if (slice.finished + sz > slice.size)
                {
                    sz = (int)(slice.size - slice.finished);
                }
                buf = new byte[sz];
                //读取的位置为，本分片的开始位置 + 本分片已经上传的数量
                fs.Position = slice.offset + slice.finished;
                fs.Read(buf, 0, buf.Length);
                fs.Close();
                slice.finished += buf.Length;
                slice.owner.finished += buf.Length;

                if (ProgressEnabled && ProgressChange != null && slice.owner.size > 0)
                {
                    ProgressChangeArgs args = new ProgressChangeArgs(slice.owner.finished, slice.owner.size);
                    ProgressChange(this, args);

                    if(args.cancelled)
                    {
                        slice.status = SliceStatus.Cancelled;
                        slice.owner.cancelled = true;
                        return NativeConst.CURL_READFUNC_ABORT;
                    }
                }
                return buf.Length;
            }
            catch (Exception ex)
            {

            }
            buf = null;
            slice.status = SliceStatus.Cancelled;
            slice.owner.cancelled = true;
            return NativeConst.CURL_READFUNC_ABORT;
        }

        protected int onProgress(BaiduPCS sender, double dltotal, double dlnow, double ultotal, double ulnow, object userdata)
        {
            if (ProgressChange != null && ultotal >= 1)
            {
                ProgressChangeArgs args = new ProgressChangeArgs((long)ulnow, (long)ultotal);
                ProgressChange(this, args);

                if (args.cancelled)
                    return -1;
            }
            return 0;
        }

    }
}
