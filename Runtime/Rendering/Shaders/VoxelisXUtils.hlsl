// 0x0000 ~ 0x0FFF -> Non-solid
// 0x1000 ~ 0xEFFF -> Solid
inline bool IsOpaque(int blk)
{
    return blk > 0;
    return (blk & 0xF000);
}
