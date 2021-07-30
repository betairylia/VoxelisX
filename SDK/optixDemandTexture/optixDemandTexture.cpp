//
// Copyright (c) 2019, NVIDIA CORPORATION. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//  * Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
//  * Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
//  * Neither the name of NVIDIA CORPORATION nor the names of its
//    contributors may be used to endorse or promote products derived
//    from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ``AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
// PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT OWNER OR
// CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
// EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
// PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
// PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY
// OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
//

#include "optixDemandTexture.h"

#include <DemandLoading/CheckerBoardImage.h>
#include <DemandLoading/DemandTexture.h>
#include <DemandLoading/DemandTextureManager.h>
#ifdef OPTIX_SAMPLE_USE_OPEN_EXR
#include <DemandLoading/EXRReader.h>
#endif
#include <DemandLoading/TextureDescriptor.h>

#include <optix.h>
#include <optix_function_table_definition.h>
#include <optix_stack_size.h>
#include <optix_stubs.h>

#include <cuda_runtime.h>

#include <sampleConfig.h>

#include <sutil/CUDAOutputBuffer.h>
#include <sutil/Camera.h>
#include <sutil/sutil.h>

#include <iomanip>
#include <iostream>
#include <memory>
#include <string>

using namespace demandLoading;

int    g_numTextureTaps   = 1;
int    g_totalLaunches    = 0;
double g_totalLaunchTime  = 0.0;
int    g_totalRequests    = 0;
double g_totalRequestTime = 0.0;
double g_idleRequestTime  = 0.0;

CUaddress_mode g_uWrapMode     = CU_TR_ADDRESS_MODE_BORDER;
CUaddress_mode g_vWrapMode     = CU_TR_ADDRESS_MODE_BORDER;
CUfilter_mode  g_filterMode    = CU_TR_FILTER_MODE_LINEAR;
CUfilter_mode  g_mipFilterMode = CU_TR_FILTER_MODE_LINEAR;
float          g_mipLevelBias  = 0.0f;

float g_diffScale = 1.0f;

int32_t g_width  = 768;
int32_t g_height = 768;

int g_textureWidth  = 2048;
int g_textureHeight = 2048;

sutil::Camera g_camera;

struct PerDeviceSampleState
{
    int32_t                     device_idx               = -1;
    OptixDeviceContext          context                  = 0;
    OptixTraversableHandle      gas_handle               = 0;  // Traversable handle for triangle AS
    CUdeviceptr                 d_gas_output_buffer      = 0;  // Triangle AS memory
    OptixModule                 ptx_module               = 0;
    OptixPipelineCompileOptions pipeline_compile_options = {};
    OptixPipeline               pipeline                 = 0;
    OptixProgramGroup           raygen_prog_group        = 0;
    OptixProgramGroup           miss_prog_group          = 0;
    OptixProgramGroup           hitgroup_prog_group      = 0;
    OptixShaderBindingTable     sbt                      = {};
    Params                      params                   = {};
    Params*                     d_params                 = nullptr;
    CUstream                    stream                   = 0;
};


template <typename T>
struct SbtRecord
{
    __align__( OPTIX_SBT_RECORD_ALIGNMENT ) char header[OPTIX_SBT_RECORD_HEADER_SIZE];
    T data;
};

typedef SbtRecord<RayGenData>   RayGenSbtRecord;
typedef SbtRecord<MissData>     MissSbtRecord;
typedef SbtRecord<HitGroupData> HitGroupSbtRecord;


void printUsageAndExit( const char* argv0 )
{
    std::cerr << "\nUsage  : " << argv0 << " [options]\n";
    std::cerr << "Options: --help | -h                         Print this usage message\n";
    std::cerr << "         --file | -f <filename>              Specify file for image output\n";
    std::cerr << "         --dim=<width>x<height>              Set image dimensions\n";
#ifdef OPTIX_SAMPLE_USE_OPEN_EXR
    std::cerr << "         --texture | -t <filename>           Texture to render (path relative to data folder). Use "
                 "checkerboard for procedural texture.\n";
#endif
    std::cerr
        << "         --textureDim=<width>x<height>       Set dimensions of procedural texture (default 2048x2048).\n";
    std::cerr << "         --squaresPerSide <n>                Number of squares per side for a procedural texture "
                 "(default 32).\n";
    std::cerr << "         --useMipmaps <true|false>           Whether to use mipmaps for a procedural texture "
                 "(default true).\n";
    std::cerr << "         --bias | -b <bias>                  Mip level bias (default 0.0)\n";
    std::cerr << "         --textureScale <s>                  Texture scale (how many times to wrap the texture "
                 "around the sphere) (default 1.0f)\n";
    std::cerr << "         --tileWidth <power of 2 or -1>      Texture tile width (default 128). -1 indicates to use "
                 "image tile width.\n";
    std::cerr << "         --filterMode <0|1|point|linear>     Texture filter mode (default linear).\n";
    std::cerr << "         --mipFilterMode <0|1|point|linear>  Texture mipmap filter mode (default linear).\n";
    std::cerr << "         --wrapModeU <0-3|wrap|clamp|mirror|border>  Texture wrap (address) mode in the U direction "
                 "(default wrap).\n";
    std::cerr << "         --wrapModeV <0-3|wrap|clamp|mirror|border>  Texture wrap (address) mode in the V direction "
                 "(default wrap).\n";
    std::cerr << "         --diffScale <n>                     How to scale the texture difference when diff render "
                 "mode is used (default 1.0).\n";
    std::cerr << "         --profileIterations <n>             Set minimum iterations to perform for profiling "
                 "(default 1).\n";
    std::cerr
        << "         --textureTaps <n>                   The number of texture taps to take per sample (default 1).\n";
    std::cerr << "\n";
    exit( 1 );
}

static CUaddress_mode getWrapMode( std::string m )
{
    if( m == "0" || m == "wrap" )
        return CU_TR_ADDRESS_MODE_WRAP;
    else if( m == "1" || m == "clamp" )
        return CU_TR_ADDRESS_MODE_CLAMP;
    else if( m == "2" || m == "mirror" )
        return CU_TR_ADDRESS_MODE_MIRROR;
    else  // "3" || "border"
        return CU_TR_ADDRESS_MODE_BORDER;
}

static CUfilter_mode getFilterMode( std::string m )
{
    if( m == "0" || m == "point" )
        return CU_TR_FILTER_MODE_POINT;
    else  // "1" || "linear"
        return CU_TR_FILTER_MODE_LINEAR;
}

static void context_log_cb( unsigned int level, const char* tag, const char* message, void* /*cbdata */ )
{
    std::cerr << "[" << std::setw( 2 ) << level << "][" << std::setw( 12 ) << tag << "]: " << message << "\n";
}


void initCameraState()
{
    float3 camEye = {-6.0f, 0.0f, 0.0f};
    g_camera.setEye( camEye );
    g_camera.setLookat( make_float3( 0.0f, 0.0f, 0.0f ) );
    g_camera.setUp( make_float3( 0.0f, 0.0f, 1.0f ) );
    g_camera.setFovY( 30.0f );
    g_camera.setAspectRatio( static_cast<float>( g_width ) / static_cast<float>( g_height ) );
}


void getDevices( std::vector<unsigned int>& devices )
{
    int32_t deviceCount = 0;
    CUDA_CHECK( cudaGetDeviceCount( &deviceCount ) );
    devices.resize( deviceCount );
    std::cout << "Total GPUs visible: " << devices.size() << std::endl;
    for( int32_t deviceIndex = 0; deviceIndex < deviceCount; ++deviceIndex )
    {
        cudaDeviceProp prop;
        CUDA_CHECK( cudaGetDeviceProperties( &prop, deviceIndex ) );
        std::cout << "\t[" << devices[deviceIndex] << "]: " << prop.name << std::endl;
        devices[deviceIndex] = deviceIndex;
    }
}


void createContext( PerDeviceSampleState& state )
{
    // Initialize CUDA on this device
    CUDA_CHECK( cudaFree( 0 ) );

    OptixDeviceContext        context;
    CUcontext                 cuCtx   = 0;  // zero means take the current context
    OptixDeviceContextOptions options = {};
    options.logCallbackFunction       = &context_log_cb;
    options.logCallbackLevel          = 4;
    OPTIX_CHECK( optixDeviceContextCreate( cuCtx, &options, &context ) );

    state.context = context;

    CUDA_CHECK( cudaStreamCreate( &state.stream ) );
}


void createContexts( std::vector<unsigned int>& devices, std::vector<PerDeviceSampleState>& states )
{
    OPTIX_CHECK( optixInit() );

    states.resize( devices.size() );

    for( unsigned int i = 0; i < devices.size(); ++i )
    {
        states[i].device_idx = devices[i];
        CUDA_CHECK( cudaSetDevice( i ) );
        createContext( states[i] );
    }
}


void buildAccel( PerDeviceSampleState& state )
{
    OptixAccelBuildOptions accel_options = {};
    accel_options.buildFlags             = OPTIX_BUILD_FLAG_ALLOW_COMPACTION;
    accel_options.operation              = OPTIX_BUILD_OPERATION_BUILD;

    // AABB build input
    OptixAabb   aabb = {-1.5f, -1.5f, -1.5f, 1.5f, 1.5f, 1.5f};
    CUdeviceptr d_aabb_buffer;
    CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &d_aabb_buffer ), sizeof( OptixAabb ) ) );
    CUDA_CHECK( cudaMemcpy( reinterpret_cast<void*>( d_aabb_buffer ), &aabb, sizeof( OptixAabb ), cudaMemcpyHostToDevice ) );

    OptixBuildInput aabb_input = {};

    aabb_input.type                               = OPTIX_BUILD_INPUT_TYPE_CUSTOM_PRIMITIVES;
    aabb_input.customPrimitiveArray.aabbBuffers   = &d_aabb_buffer;
    aabb_input.customPrimitiveArray.numPrimitives = 1;

    uint32_t aabb_input_flags[1]                  = {OPTIX_GEOMETRY_FLAG_NONE};
    aabb_input.customPrimitiveArray.flags         = aabb_input_flags;
    aabb_input.customPrimitiveArray.numSbtRecords = 1;

    OptixAccelBufferSizes gas_buffer_sizes;
    OPTIX_CHECK( optixAccelComputeMemoryUsage( state.context, &accel_options, &aabb_input, 1, &gas_buffer_sizes ) );
    CUdeviceptr d_temp_buffer_gas;
    CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &d_temp_buffer_gas ), gas_buffer_sizes.tempSizeInBytes ) );

    // non-compacted output
    CUdeviceptr d_buffer_temp_output_gas_and_compacted_size;
    size_t      compactedSizeOffset = roundUp<size_t>( gas_buffer_sizes.outputSizeInBytes, 8ull );
    CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &d_buffer_temp_output_gas_and_compacted_size ), compactedSizeOffset + 8 ) );

    OptixAccelEmitDesc emitProperty = {};
    emitProperty.type               = OPTIX_PROPERTY_TYPE_COMPACTED_SIZE;
    emitProperty.result = ( CUdeviceptr )( (char*)d_buffer_temp_output_gas_and_compacted_size + compactedSizeOffset );

    OPTIX_CHECK( optixAccelBuild( state.context,
                                  0,  // CUDA stream
                                  &accel_options, &aabb_input,
                                  1,  // num build inputs
                                  d_temp_buffer_gas, gas_buffer_sizes.tempSizeInBytes, d_buffer_temp_output_gas_and_compacted_size,
                                  gas_buffer_sizes.outputSizeInBytes, &state.gas_handle,
                                  &emitProperty,  // emitted property list
                                  1               // num emitted properties
                                  ) );

    CUDA_CHECK( cudaFree( (void*)d_temp_buffer_gas ) );
    CUDA_CHECK( cudaFree( (void*)d_aabb_buffer ) );

    size_t compacted_gas_size;
    CUDA_CHECK( cudaMemcpy( &compacted_gas_size, (void*)emitProperty.result, sizeof( size_t ), cudaMemcpyDeviceToHost ) );

    if( compacted_gas_size < gas_buffer_sizes.outputSizeInBytes )
    {
        CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &state.d_gas_output_buffer ), compacted_gas_size ) );

        // use handle as input and output
        OPTIX_CHECK( optixAccelCompact( state.context, 0, state.gas_handle, state.d_gas_output_buffer,
                                        compacted_gas_size, &state.gas_handle ) );

        CUDA_CHECK( cudaFree( (void*)d_buffer_temp_output_gas_and_compacted_size ) );
    }
    else
    {
        state.d_gas_output_buffer = d_buffer_temp_output_gas_and_compacted_size;
    }
}


void createModule( PerDeviceSampleState& state )
{
    OptixModuleCompileOptions module_compile_options = {};
    module_compile_options.maxRegisterCount          = 100;
    module_compile_options.optLevel                  = OPTIX_COMPILE_OPTIMIZATION_DEFAULT;
    module_compile_options.debugLevel                = OPTIX_COMPILE_DEBUG_LEVEL_LINEINFO;

    state.pipeline_compile_options.usesMotionBlur        = false;
    state.pipeline_compile_options.traversableGraphFlags = OPTIX_TRAVERSABLE_GRAPH_FLAG_ALLOW_SINGLE_GAS;
    state.pipeline_compile_options.numPayloadValues      = 3;
    state.pipeline_compile_options.numAttributeValues    = 6;
    state.pipeline_compile_options.exceptionFlags = OPTIX_EXCEPTION_FLAG_NONE;  // TODO: should be OPTIX_EXCEPTION_FLAG_STACK_OVERFLOW;
    state.pipeline_compile_options.pipelineLaunchParamsVariableName = "params";

    const std::string ptx = sutil::getPtxString( OPTIX_SAMPLE_NAME, OPTIX_SAMPLE_DIR, "optixDemandTexture.cu" );
    char              log[2048];
    size_t            sizeof_log = sizeof( log );

    OPTIX_CHECK_LOG( optixModuleCreateFromPTX( state.context, &module_compile_options, &state.pipeline_compile_options,
                                               ptx.c_str(), ptx.size(), log, &sizeof_log, &state.ptx_module ) );
}


void createProgramGroups( PerDeviceSampleState& state )
{
    OptixProgramGroupOptions program_group_options = {};  // Initialize to zeros

    OptixProgramGroupDesc raygen_prog_group_desc    = {};  //
    raygen_prog_group_desc.kind                     = OPTIX_PROGRAM_GROUP_KIND_RAYGEN;
    raygen_prog_group_desc.raygen.module            = state.ptx_module;
    raygen_prog_group_desc.raygen.entryFunctionName = "__raygen__rg";
    char   log[2048];
    size_t sizeof_log = sizeof( log );
    OPTIX_CHECK_LOG( optixProgramGroupCreate( state.context, &raygen_prog_group_desc,
                                              1,  // num program groups
                                              &program_group_options, log, &sizeof_log, &state.raygen_prog_group ) );

    OptixProgramGroupDesc miss_prog_group_desc  = {};
    miss_prog_group_desc.kind                   = OPTIX_PROGRAM_GROUP_KIND_MISS;
    miss_prog_group_desc.miss.module            = state.ptx_module;
    miss_prog_group_desc.miss.entryFunctionName = "__miss__ms";
    sizeof_log                                  = sizeof( log );
    OPTIX_CHECK_LOG( optixProgramGroupCreate( state.context, &miss_prog_group_desc,
                                              1,  // num program groups
                                              &program_group_options, log, &sizeof_log, &state.miss_prog_group ) );

    OptixProgramGroupDesc hitgroup_prog_group_desc        = {};
    hitgroup_prog_group_desc.kind                         = OPTIX_PROGRAM_GROUP_KIND_HITGROUP;
    hitgroup_prog_group_desc.hitgroup.moduleCH            = state.ptx_module;
    hitgroup_prog_group_desc.hitgroup.entryFunctionNameCH = "__closesthit__ch";
    hitgroup_prog_group_desc.hitgroup.moduleAH            = nullptr;
    hitgroup_prog_group_desc.hitgroup.entryFunctionNameAH = nullptr;
    hitgroup_prog_group_desc.hitgroup.moduleIS            = state.ptx_module;
    hitgroup_prog_group_desc.hitgroup.entryFunctionNameIS = "__intersection__is";
    sizeof_log                                            = sizeof( log );
    OPTIX_CHECK_LOG( optixProgramGroupCreate( state.context, &hitgroup_prog_group_desc,
                                              1,  // num program groups
                                              &program_group_options, log, &sizeof_log, &state.hitgroup_prog_group ) );
}


void createPipeline( PerDeviceSampleState& state )
{
    const uint32_t    max_trace_depth  = 1;
    OptixProgramGroup program_groups[] = {state.raygen_prog_group, state.miss_prog_group, state.hitgroup_prog_group};

    OptixPipelineLinkOptions pipeline_link_options = {};
    pipeline_link_options.maxTraceDepth            = max_trace_depth;
    pipeline_link_options.debugLevel               = OPTIX_COMPILE_DEBUG_LEVEL_FULL;
    char   log[2048];
    size_t sizeof_log = sizeof( log );
    OPTIX_CHECK_LOG( optixPipelineCreate( state.context, &state.pipeline_compile_options, &pipeline_link_options,
                                          program_groups, sizeof( program_groups ) / sizeof( program_groups[0] ), log,
                                          &sizeof_log, &state.pipeline ) );

    OptixStackSizes stack_sizes = {};
    for( auto& prog_group : program_groups )
    {
        OPTIX_CHECK( optixUtilAccumulateStackSizes( prog_group, &stack_sizes ) );
    }

    uint32_t direct_callable_stack_size_from_traversal;
    uint32_t direct_callable_stack_size_from_state;
    uint32_t continuation_stack_size;
    OPTIX_CHECK( optixUtilComputeStackSizes( &stack_sizes, max_trace_depth,
                                             0,  // maxCCDepth
                                             0,  // maxDCDEpth
                                             &direct_callable_stack_size_from_traversal,
                                             &direct_callable_stack_size_from_state, &continuation_stack_size ) );
    OPTIX_CHECK( optixPipelineSetStackSize( state.pipeline, direct_callable_stack_size_from_traversal,
                                            direct_callable_stack_size_from_state, continuation_stack_size,
                                            1  // maxTraversableDepth
                                            ) );
}


void createSBT( PerDeviceSampleState& state, const DemandTexture& texture, float texture_scale, float texture_lod )
{
    CUdeviceptr  raygen_record;
    const size_t raygen_record_size = sizeof( RayGenSbtRecord );
    CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &raygen_record ), raygen_record_size ) );
    RayGenSbtRecord rg_sbt = {};
    OPTIX_CHECK( optixSbtRecordPackHeader( state.raygen_prog_group, &rg_sbt ) );
    CUDA_CHECK( cudaMemcpy( reinterpret_cast<void*>( raygen_record ), &rg_sbt, raygen_record_size, cudaMemcpyHostToDevice ) );

    CUdeviceptr miss_record;
    size_t      miss_record_size = sizeof( MissSbtRecord );
    CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &miss_record ), miss_record_size ) );
    MissSbtRecord ms_sbt;
    ms_sbt.data = {0.05f, 0.05f, 0.3f};
    OPTIX_CHECK( optixSbtRecordPackHeader( state.miss_prog_group, &ms_sbt ) );
    CUDA_CHECK( cudaMemcpy( reinterpret_cast<void*>( miss_record ), &ms_sbt, miss_record_size, cudaMemcpyHostToDevice ) );

    // The demand-loaded texture id is passed to the closest hit program via the hitgroup record.
    CUdeviceptr hitgroup_record;
    size_t      hitgroup_record_size = sizeof( HitGroupSbtRecord );
    CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &hitgroup_record ), hitgroup_record_size ) );
    HitGroupSbtRecord hg_sbt;
    hg_sbt.data = {1.5f /*radius*/, texture.getId(), texture_scale, texture_lod};
    OPTIX_CHECK( optixSbtRecordPackHeader( state.hitgroup_prog_group, &hg_sbt ) );
    CUDA_CHECK( cudaMemcpy( reinterpret_cast<void*>( hitgroup_record ), &hg_sbt, hitgroup_record_size, cudaMemcpyHostToDevice ) );

    state.sbt.raygenRecord                = raygen_record;
    state.sbt.missRecordBase              = miss_record;
    state.sbt.missRecordStrideInBytes     = sizeof( MissSbtRecord );
    state.sbt.missRecordCount             = 1;
    state.sbt.hitgroupRecordBase          = hitgroup_record;
    state.sbt.hitgroupRecordStrideInBytes = sizeof( HitGroupSbtRecord );
    state.sbt.hitgroupRecordCount         = 1;
}

void cleanupState( PerDeviceSampleState& state )
{
    OPTIX_CHECK( optixPipelineDestroy( state.pipeline ) );
    OPTIX_CHECK( optixProgramGroupDestroy( state.raygen_prog_group ) );
    OPTIX_CHECK( optixProgramGroupDestroy( state.miss_prog_group ) );
    OPTIX_CHECK( optixProgramGroupDestroy( state.hitgroup_prog_group ) );
    OPTIX_CHECK( optixModuleDestroy( state.ptx_module ) );
    OPTIX_CHECK( optixDeviceContextDestroy( state.context ) );

    CUDA_CHECK( cudaFree( reinterpret_cast<void*>( state.sbt.raygenRecord ) ) );
    CUDA_CHECK( cudaFree( reinterpret_cast<void*>( state.sbt.missRecordBase ) ) );
    CUDA_CHECK( cudaFree( reinterpret_cast<void*>( state.sbt.hitgroupRecordBase ) ) );
    CUDA_CHECK( cudaFree( reinterpret_cast<void*>( state.d_gas_output_buffer ) ) );
    CUDA_CHECK( cudaFree( reinterpret_cast<void*>( state.d_params ) ) );

    if( state.params.nonDemandTexture != 0 )
        CUDA_CHECK( cudaDestroyTextureObject( state.params.nonDemandTexture ) );
    if( state.params.nonDemandTextureArray != 0 )
        CUDA_CHECK( cudaFreeMipmappedArray( state.params.nonDemandTextureArray ) );
}


TextureDescriptor makeTextureDescription()
{
    TextureDescriptor texDesc{};
    texDesc.addressMode[0]   = g_uWrapMode;
    texDesc.addressMode[1]   = g_vWrapMode;
    texDesc.filterMode       = g_filterMode;
    texDesc.mipmapFilterMode = g_mipFilterMode;
    texDesc.maxAnisotropy    = 16;

    return texDesc;
}


void initLaunchParams( PerDeviceSampleState& state, unsigned int numDevices )
{
    state.params.image_width    = g_width;
    state.params.image_height   = g_height;
    state.params.origin_x       = g_width / 2;
    state.params.origin_y       = g_height / 2;
    state.params.handle         = state.gas_handle;
    state.params.device_idx     = state.device_idx;
    state.params.num_devices    = numDevices;
    state.params.mipLevelBias   = g_mipLevelBias;
    state.params.diffScale      = g_diffScale;
    state.params.numTextureTaps = g_numTextureTaps;

    // state.params.nonDemandTextureArray and state.params.nonDemandTexture set in makeStaticTexture

    state.params.eye = g_camera.eye();
    g_camera.UVWFrame( state.params.U, state.params.V, state.params.W );

    if( state.d_params == nullptr )
        CUDA_CHECK( cudaMalloc( reinterpret_cast<void**>( &state.d_params ), sizeof( Params ) ) );
}


void performLaunches( sutil::CUDAOutputBuffer<uchar4>& output_buffer, std::vector<PerDeviceSampleState>& states, DemandTextureManager& textureManager )
{
    auto startTime = std::chrono::steady_clock::now();

    for( auto& state : states )
    {
        CUDA_CHECK( cudaSetDevice( state.device_idx ) );

        // Synchronize any new texture samplers and texture info to device memory, and map output buffer.
        textureManager.launchPrepare( state.device_idx, state.params.demandTextureContext );
        state.params.result_buffer = output_buffer.map();

        // Update launch parameters
        initLaunchParams( state, static_cast<unsigned int>( states.size() ) );
        CUDA_CHECK( cudaMemcpyAsync( reinterpret_cast<void*>( state.d_params ), &state.params, sizeof( Params ),
                                     cudaMemcpyHostToDevice, state.stream ) );

        // Peform the launch
        OPTIX_CHECK( optixLaunch( state.pipeline, state.stream, reinterpret_cast<CUdeviceptr>( state.d_params ),
                                  sizeof( Params ), &state.sbt,
                                  state.params.image_height / static_cast<unsigned int>( states.size() ),  // launch height (launch is split across GPUs)
                                  state.params.image_width,  // launch width
                                  1                          // launch depth
                                  ) );

        output_buffer.unmap();
    }
    for( auto& state : states )
    {
        CUDA_CHECK( cudaSetDevice( state.device_idx ) );
        CUDA_SYNC_CHECK();
    }

    g_totalLaunches++;
    g_totalLaunchTime += std::chrono::duration<double>( std::chrono::steady_clock::now() - startTime ).count();
}

int processTextureRequests( DemandTextureManager& textureManager )
{
    auto startTime = std::chrono::steady_clock::now();

    int numRequests = textureManager.processRequests();

    if( numRequests > 0 )
    {
        g_totalRequests += numRequests;
        g_totalRequestTime += std::chrono::duration<double>( std::chrono::steady_clock::now() - startTime ).count();
    }
    else
    {
        g_idleRequestTime += std::chrono::duration<double>( std::chrono::steady_clock::now() - startTime ).count();
    }
    return numRequests;
}

void printTimingStats()
{
    std::cout << "Launches: " << g_totalLaunches << "\n";
    std::cout << "Texture taps per launch: " << g_numTextureTaps << "\n";
    if( g_totalLaunches > 0 )
    {
        std::cout << "Avg. launch time: " << ( 1000.0 * g_totalLaunchTime / g_totalLaunches ) << " ms.\n";
        std::cout << "Avg. time processing empty requests: " << ( 1000.0 * g_idleRequestTime / g_totalLaunches )
                  << " ms / launch.\n";
    }
    if( g_totalRequests > 0 )
    {
        std::cout << "Texture tile requests: " << g_totalRequests << "\n";
        std::cout << "Avg. tile request time: " << ( 1000.0 * g_totalRequestTime / g_totalRequests ) << " ms.\n";
    }
    std::cout << "\n";
}

int main( int argc, char* argv[] )
{
    float textureScale      = 1.0f;
    int   squaresPerSide    = 32;
    int   tileWidth         = 128;
    int   tileHeight        = 128;
    bool  useMipmaps        = true;
    int   profileIterations = 0;

    std::string outfile;

    // Image credit: CC0Textures.com (https://cc0textures.com/view.php?tex=Bricks12)
    // Licensed under the Creative Commons CC0 License.
    std::string textureFile = "Textures/Bricks12_col.exr";  // use --texture "" for procedural texture

    for( int i = 1; i < argc; ++i )
    {
        const std::string arg( argv[i] );
        bool              lastArg = ( i == argc - 1 );

        if( arg == "--help" || arg == "-h" )
        {
            printUsageAndExit( argv[0] );
        }
        else if( ( arg == "--file" || arg == "-f" ) && !lastArg )
        {
            outfile = argv[++i];
        }
        else if( arg.substr( 0, 6 ) == "--dim=" )
        {
            sutil::parseDimensions( arg.substr( 6 ).c_str(), g_width, g_height );
        }
        else if( ( arg == "--bias" || arg == "-b" ) && !lastArg )
        {
            g_mipLevelBias = static_cast<float>( atof( argv[++i] ) );
        }
        else if( ( arg == "--texture" || arg == "-t" ) && !lastArg )
        {
            textureFile = argv[++i];
        }
        else if( arg == "--textureScale" && !lastArg )
        {
            textureScale = static_cast<float>( atof( argv[++i] ) );
        }
        else if( arg == "--squaresPerSide" && !lastArg )
        {
            squaresPerSide = atoi( argv[++i] );
        }
        else if( arg == "--useMipmaps" && !lastArg )
        {
            useMipmaps = std::string( argv[++i] ) != "false";
        }
        else if( arg.substr( 0, 10 ) == "--tileDim=" )
        {
            sutil::parseDimensions( arg.substr( 10 ).c_str(), tileWidth, tileHeight );
        }
        else if( arg == "--tileWidth" && !lastArg )
        {
            tileWidth = atoi( argv[++i] );
        }
        else if( arg == "--wrapModeU" && !lastArg )
        {
            g_uWrapMode = getWrapMode( argv[++i] );
        }
        else if( arg == "--wrapModeV" && !lastArg )
        {
            g_vWrapMode = getWrapMode( argv[++i] );
        }
        else if( arg == "--filterMode" && !lastArg )
        {
            g_filterMode = getFilterMode( argv[++i] );
        }
        else if( arg == "--mipFilterMode" && !lastArg )
        {
            g_mipFilterMode = getFilterMode( argv[++i] );
        }
        else if( arg == "--profileIterations" && !lastArg )
        {
            profileIterations = atoi( argv[++i] );
        }
        else if( arg == "--diffScale" && !lastArg )
        {
            g_diffScale = static_cast<float>( atof( argv[++i] ) );
        }
        else if( arg.substr( 0, 13 ) == "--textureDim=" )
        {
            sutil::parseDimensions( arg.substr( 13 ).c_str(), g_textureWidth, g_textureHeight );
        }
        else if( arg == "--textureTaps" && !lastArg )
        {
            g_numTextureTaps = atoi( argv[++i] );
        }
        else
        {
            std::cerr << "Unknown option '" << arg << "'\n";
            printUsageAndExit( argv[0] );
        }
    }

    try
    {
        initCameraState();

        std::vector<unsigned int> availableDevices;
        getDevices( availableDevices );

        std::vector<PerDeviceSampleState> states;
        createContexts( availableDevices, states );

        // Initialize DemandTextureManager and create a demand-loaded texture.
        // The texture id is passed to the closest hit shader via a hit group record in the SBT.
        // The texture sampler array (indexed by texture id) is passed as a launch parameter.
        DemandTextureManagerConfig config{};
        config.numPages            = 1024 * 1024;  // max virtual pages
        config.maxRequestedPages   = 1024;         // max requests to pull from device when calling pullRequests
        config.maxFilledPages      = 2048;         // number of slots to push mappings back to device
        config.maxStalePages       = 2048;         // max stale pages to pull from the device when calling pullRequests
        config.maxInvalidatedPages = 2048;         // max slots to push invalidated pages back to device
        std::shared_ptr<DemandTextureManager> textureManager( createDemandTextureManager( availableDevices, config ), destroyDemandTextureManager );

        std::unique_ptr<ImageReader> imageReader;

        // If "useMipmaps" is disabled, use the checkboard image (otherwise the number of miplevels is determined by the EXR file.)
        if (!useMipmaps)
            textureFile = "checkerboard";

// Make an exr reader or a procedural texture reader based on the textureFile name
#ifdef OPTIX_SAMPLE_USE_OPEN_EXR
        if( !textureFile.empty() && textureFile != "checkerboard" )
        {
            std::string textureFilename( sutil::sampleDataFilePath( textureFile.c_str() ) );
            imageReader = std::unique_ptr<ImageReader>( new EXRReader( textureFilename.c_str() ) );
        }
#endif
        if( imageReader == nullptr )
        {
            imageReader = std::unique_ptr<ImageReader>(
                new CheckerBoardImage( g_textureWidth, g_textureHeight, squaresPerSide, useMipmaps ) );
        }

        // Create a demand-loaded texture
        TextureDescriptor    texDesc = makeTextureDescription();
        const DemandTexture& texture = textureManager->createTexture( std::move( imageReader ), texDesc );

        // Set up OptiX per-device states
        for( auto& state : states )
        {
            CUDA_CHECK( cudaSetDevice( state.device_idx ) );
            buildAccel( state );
            createModule( state );
            createProgramGroups( state );
            createPipeline( state );
            createSBT( state, texture, textureScale, 0.f /*textureLod*/ );
        }

        // Create the output buffer to hold the rendered image
        sutil::CUDAOutputBuffer<uchar4> outputBuffer( sutil::CUDAOutputBufferType::ZERO_COPY, g_width, g_height );

        // Perform launches (launch until there are no more requests to fill)
        int numFilled = 0;
        do
        {
            performLaunches( outputBuffer, states, *textureManager );
            numFilled = processTextureRequests( *textureManager );
        } while( numFilled > 0 || g_totalLaunches < profileIterations );

        printTimingStats();

        // Display result image
        {
            sutil::ImageBuffer buffer;
            buffer.data         = outputBuffer.getHostPointer();
            buffer.width        = g_width;
            buffer.height       = g_height;
            buffer.pixel_format = sutil::BufferImageFormat::UNSIGNED_BYTE4;
            if( outfile.empty() )
                sutil::displayBufferWindow( argv[0], buffer );
            else
                sutil::saveImage( outfile.c_str(), buffer, false );
        }

        // Clean up the states, deleting their resources
        for( auto& state : states )
            cleanupState( state );
    }
    catch( std::exception& e )
    {
        std::cerr << "Caught exception: " << e.what() << "\n";
        return 1;
    }
    return 0;
}
