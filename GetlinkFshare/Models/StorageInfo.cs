namespace GetlinkFshare.Models
{
    public class StorageInfo
    {
        public required string UsedStorage { get; set; }
        public required string AvailableToday { get; set; }
        // *** ĐÃ THÊM: Trường để lưu ngày giờ cập nhật ***
        public DateTime? LastUpdated { get; set; }
    }
}
