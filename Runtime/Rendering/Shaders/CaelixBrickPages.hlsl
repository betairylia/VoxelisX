#ifndef CAELIX_BRICK_PAGES_INCLUDED
#define CAELIX_BRICK_PAGES_INCLUDED

// Brick pool pages. One GraphicsBuffer cannot exceed SystemInfo.maxGraphicsBufferSize (~3.9 GB),
// and a large scene holds more brick records than that, so the pool is split into up to four
// buffers and every sector names the page its bricks live in. The page is selected by the including
// file (from the instance record in the ray query kernel, from the instance's property block in the
// DXR hit group) and every brick load switches on it. Must match CaelixBrickPool.MaxNamedPages.
#define CAELIX_BRICK_PAGES 4
ByteAddressBuffer g_bricks0;
ByteAddressBuffer g_bricks1;
ByteAddressBuffer g_bricks2;
ByteAddressBuffer g_bricks3;
static uint _CaelixBrickPage;

uint CaelixBrickPageLoad(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load(byteAddress);
        case 1u: return g_bricks1.Load(byteAddress);
        case 2u: return g_bricks2.Load(byteAddress);
        default: return g_bricks3.Load(byteAddress);
    }
}

uint2 CaelixBrickPageLoad2(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load2(byteAddress);
        case 1u: return g_bricks1.Load2(byteAddress);
        case 2u: return g_bricks2.Load2(byteAddress);
        default: return g_bricks3.Load2(byteAddress);
    }
}

uint64_t CaelixBrickPageLoad64(uint byteAddress)
{
    switch (_CaelixBrickPage)
    {
        case 0u: return g_bricks0.Load<uint64_t>(byteAddress);
        case 1u: return g_bricks1.Load<uint64_t>(byteAddress);
        case 2u: return g_bricks2.Load<uint64_t>(byteAddress);
        default: return g_bricks3.Load<uint64_t>(byteAddress);
    }
}

#define CAELIX_BRICKS_LOAD(byteAddress) CaelixBrickPageLoad(byteAddress)
#define CAELIX_BRICKS_LOAD2(byteAddress) CaelixBrickPageLoad2(byteAddress)
#define CAELIX_BRICKS_LOAD64(byteAddress) CaelixBrickPageLoad64(byteAddress)

#endif
