start "BobChainA" "C:\Program Files\Bitcoin\bitcoin-qt.exe" -datadir=%cd%/Bob/ChainA -config=%cd%/Bob/ChainA/bitcoin.conf
start "BobChainB" "C:\Program Files\Bitcoin\bitcoin-qt.exe" -datadir=%cd%/Bob/ChainB -config=%cd%/Bob/ChainB/bitcoin.conf